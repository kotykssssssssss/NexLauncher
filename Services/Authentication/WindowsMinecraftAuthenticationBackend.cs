using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CmlLib.Core.Auth.Microsoft;
using XboxAuthNet.Game.Authenticators;
using XboxAuthNet.OAuth.CodeFlow;
using XboxAuthNet.OAuth.CodeFlow.Parameters;

namespace NexLauncher.Services.Authentication;

/// <summary>Preserves CmlLib's existing supported Windows WebView2 configuration.</summary>
public sealed class WindowsMinecraftAuthenticationBackend : CmlMinecraftAuthenticationBackend
{
    protected override Task<Action<JsonObject, string>?> ConfigureOAuthAsync(
        ICompositeAuthenticator authenticator, JsonObject storedAccounts,
        string? accountId, bool interactive, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (interactive)
            authenticator.AddForceMicrosoftOAuthForJE(oauth => oauth.Interactive(
                builder => builder.WithUITitle("Вход в NexLauncher"),
                new CodeFlowAuthorizationParameter { Prompt = MicrosoftOAuthPromptModes.SelectAccount }));
        else
            authenticator.AddMicrosoftOAuthForJE(oauth => oauth.Silent());
        return Task.FromResult<Action<JsonObject, string>?>(null);
    }
}
