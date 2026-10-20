using System;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using XboxAuthNet.Game.Authenticators;
using XboxAuthNet.Game.Msal;

namespace NexLauncher.Services.Authentication;

/// <summary>System-browser MSAL OAuth; the cache travels in the existing encrypted account transaction.</summary>
public sealed class BrowserMinecraftAuthenticationBackend(Func<Task<string>> clientId) : CmlMinecraftAuthenticationBackend
{
    public const string CacheProperty = "nexlauncher.msal.v1";

    public static string ValidateClientId(string? value) => Guid.TryParse(value, out var id) && id != Guid.Empty
        ? id.ToString("D")
        : throw new AuthenticationConfigurationException();

    public static IPublicClientApplication CreateApplication(string id) =>
        MsalClientHelper.CreateDefaultApplicationBuilder(ValidateClientId(id)).Build();

    public static JsonObject WithoutBrowserCache(JsonObject source)
    {
        var copy = (JsonObject)source.DeepClone();
        foreach (var entry in copy)
            if (entry.Value is JsonObject account) account.Remove(CacheProperty);
        return copy;
    }

    protected override async Task<Action<JsonObject, string>?> ConfigureOAuthAsync(
        ICompositeAuthenticator authenticator, JsonObject storedAccounts,
        string? accountId, bool interactive, CancellationToken token)
    {
        var app = CreateApplication(await clientId().ConfigureAwait(false));
        var cache = AttachCache(app, accountId is null ? null : storedAccounts[accountId]?[CacheProperty]?.GetValue<string>());
        token.ThrowIfCancellationRequested();
        // Silent renewal never falls back to opening a browser.
        authenticator.AddMsalOAuth(app, oauth => interactive
            ? oauth.Interactive(builder => builder.WithUseEmbeddedWebView(false).WithPrompt(Prompt.SelectAccount))
            : oauth.Silent());
        return (result, uuid) => CommitBrowserCache(storedAccounts, result, uuid, cache());
    }

    public static void CommitBrowserCache(JsonObject previous, JsonObject result, string uuid, string? cache)
    {
        if (string.IsNullOrEmpty(cache) || result[uuid] is not JsonObject selected)
            throw new InvalidOperationException("Microsoft не вернул cache выбранного аккаунта.");
        foreach (var entry in previous)
            if (entry.Key != uuid && entry.Value?[CacheProperty] is { } saved && result[entry.Key] is JsonObject other)
                other[CacheProperty] = saved.DeepClone();
        selected[CacheProperty] = cache;
    }

    public static Func<string?> AttachCache(IPublicClientApplication app, string? initial)
    {
        var serialized = initial;
        app.UserTokenCache.SetBeforeAccess(args =>
        {
            if (serialized is null) return;
            if (serialized.Length > 4 * 1024 * 1024) throw new InvalidOperationException("MSAL cache exceeds limit.");
            var bytes = Convert.FromBase64String(serialized);
            try { args.TokenCache.DeserializeMsalV3(bytes, shouldClearExistingCache: true); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        });
        app.UserTokenCache.SetAfterAccess(args =>
        {
            if (!args.HasStateChanged) return;
            var bytes = args.TokenCache.SerializeMsalV3();
            try { serialized = Convert.ToBase64String(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        });
        return () => serialized;
    }
}

public sealed class AuthenticationConfigurationException() : InvalidOperationException(
    "В этой сборке не настроен Microsoft Client ID NexLauncher. Вход через браузер станет доступен после регистрации приложения и разрешения Minecraft Services. Windows-вход через WebView2 продолжает работать.");
