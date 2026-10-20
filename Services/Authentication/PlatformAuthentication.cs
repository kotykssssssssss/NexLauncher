using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace NexLauncher.Services.Authentication;

public static class PlatformAuthentication
{
    public static IMinecraftAuthenticationBackend Backend(string dataDirectory) => OperatingSystem.IsWindows()
        ? new WindowsMinecraftAuthenticationBackend()
        : new BrowserMinecraftAuthenticationBackend(async () =>
        {
            var bundled = typeof(PlatformAuthentication).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(x => x.Key == "NexLauncher.MicrosoftClientId")?.Value;
            if (!string.IsNullOrWhiteSpace(bundled)) return bundled;
            return (await new ConfigurationStore(dataDirectory).LoadAsync().ConfigureAwait(false)).MicrosoftClientId;
        });

    public static IAccountDataProtector Protector(string dataDirectory) => OperatingSystem.IsWindows()
        ? new WindowsAccountDataProtector()
        : new SystemAccountDataProtector(new SystemCredentialKeyStore(Path.Combine(dataDirectory, "auth")));
}
