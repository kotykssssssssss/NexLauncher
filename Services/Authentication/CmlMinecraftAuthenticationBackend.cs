using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CmlLib.Core.Auth.Microsoft;
using CmlLib.Core.Auth.Microsoft.Sessions;
using NexLauncher.Models;
using XboxAuthNet.Game.Accounts;
using XboxAuthNet.Game.Accounts.JsonStorage;
using XboxAuthNet.OAuth.CodeFlow;
using XboxAuthNet.OAuth.CodeFlow.Parameters;

namespace NexLauncher.Services.Authentication;

/// <summary>Shared profile, ownership, Xbox and Minecraft pipeline for platform OAuth providers.</summary>
public abstract class CmlMinecraftAuthenticationBackend : IMinecraftAuthenticationBackend
{
    public IReadOnlyList<LauncherAccount> GetAccounts(JsonObject storedAccounts)
    {
        try
        {
            var manager = CreateManager(new MemoryJsonStorage(BrowserMinecraftAuthenticationBackend.WithoutBrowserCache(storedAccounts)));
            var accounts = manager.GetAccounts().OfType<JEGameAccount>().ToArray();
            if (accounts.Length != storedAccounts.Count)
                throw new InvalidOperationException();
            return Array.AsReadOnly(accounts.Select(account =>
            {
                var profile = account.Profile;
                if (string.IsNullOrWhiteSpace(account.Identifier) || !storedAccounts.ContainsKey(account.Identifier) ||
                    string.IsNullOrWhiteSpace(profile?.Username) || string.IsNullOrWhiteSpace(profile.UUID))
                    throw new InvalidOperationException();
                var skin = profile.Skins.FirstOrDefault(item => item.State == "ACTIVE")?.Url;
                return new LauncherAccount(account.Identifier, profile.Username, profile.UUID, skin);
            }).ToArray());
        }
        catch (Exception)
        {
            throw new AccountStorageException("Сохранённый профиль аккаунта повреждён. Исходное защищённое хранилище не изменено.");
        }
    }

    public Task<MinecraftAuthenticationResult> AuthenticateAsync(
        JsonObject storedAccounts, string? accountId, bool interactive, CancellationToken cancellationToken)
    {
        var candidate = (JsonObject)storedAccounts.DeepClone();
        // The native CmlLib OAuth window creates its own STA thread. Avoid capturing Avalonia's context.
        return Task.Run(() => AuthenticateCoreAsync(candidate, accountId, interactive, cancellationToken), cancellationToken);
    }

    private async Task<MinecraftAuthenticationResult> AuthenticateCoreAsync(
        JsonObject storedAccounts, string? accountId, bool interactive, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(interactive ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(90));
            using var http = new HttpClient(new CancellationAwareHandler(timeout.Token))
            {
                Timeout = TimeSpan.FromSeconds(45)
            };
            var storage = new MemoryJsonStorage(BrowserMinecraftAuthenticationBackend.WithoutBrowserCache(storedAccounts));
            var manager = CreateManager(storage);
            // Load before NewAccount: the library lazily initializes its account list.
            var existing = manager.GetAccounts();
            var account = accountId is null ? manager.NewAccount() : existing.GetAccount(accountId);
            var handler = new JELoginHandlerBuilder().WithHttpClient(http).WithAccountManager(manager).Build();
            var authenticator = handler.CreateAuthenticator(account, timeout.Token);
            var finish = await ConfigureOAuthAsync(authenticator, storedAccounts, accountId, interactive, timeout.Token).ConfigureAwait(false);
            authenticator.AddForceXboxAuthForJE(xbox => xbox.Basic());
            // Refresh the profile and explicitly check ownership before each launch.
            authenticator.AddForceJEAuthenticator(je => je.WithGameOwnershipChecker().Build());
            var session = await authenticator.ExecuteForLauncherAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(session.Username) || string.IsNullOrWhiteSpace(session.UUID) ||
                string.IsNullOrWhiteSpace(session.AccessToken) || session.UserType != "msa")
                throw new JEAuthException("Missing Minecraft session.");
            var result = storage.Copy();
            finish?.Invoke(result, session.UUID);
            return new MinecraftAuthenticationResult(session, result);
        }
        catch (Exception exception)
        {
            throw AuthenticationErrors.ForUser(exception, cancellationToken);
        }
    }

    protected abstract Task<Action<JsonObject, string>?> ConfigureOAuthAsync(
        XboxAuthNet.Game.Authenticators.ICompositeAuthenticator authenticator, JsonObject storedAccounts,
        string? accountId, bool interactive, CancellationToken token);

    private static JsonXboxGameAccountManager CreateManager(MemoryJsonStorage storage) =>
        new(storage, JEGameAccount.FromSessionStorage, JsonXboxGameAccountManager.DefaultSerializerOption);

    /// <summary>CmlLib's automatic account saves remain in memory until the service commits the transaction.</summary>
    private sealed class MemoryJsonStorage(JsonObject initial) : IJsonStorage
    {
        private JsonObject _accounts = (JsonObject)initial.DeepClone();
        public JsonNode ReadAsJsonNode() => _accounts.DeepClone();
        public void Write(JsonNode node, JsonSerializerOptions? serializerOptions) =>
            _accounts = (JsonObject)node.DeepClone();
        public JsonObject Copy() => (JsonObject)_accounts.DeepClone();
    }

    private sealed class CancellationAwareHandler(CancellationToken operationToken) : DelegatingHandler(new HttpClientHandler())
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(operationToken, cancellationToken);
            return await base.SendAsync(request, linked.Token).ConfigureAwait(false);
        }
    }
}
