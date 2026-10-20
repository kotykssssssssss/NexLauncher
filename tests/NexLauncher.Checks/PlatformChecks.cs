using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CmlLib.Core;
using CmlLib.Core.Auth.Microsoft.Sessions;
using CmlLib.Core.Java;
using CmlLib.Core.ProcessBuilder;
using CmlLib.Core.Rules;
using CmlLib.Core.Version;
using Microsoft.Identity.Client;
using NexLauncher.Services;
using NexLauncher.Services.Authentication;
using NexLauncher.Services.Storage;

internal static class PlatformChecks
{
    private const string ClientId = "12345678-1234-4234-8234-123456789abc"; // fixture only; never used for live OAuth
    private const string Uuid = "123456781234423482341234567890ab";
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        check(OperatingSystem.IsWindows()
                ? PlatformAuthentication.Backend(root) is WindowsMinecraftAuthenticationBackend
                : PlatformAuthentication.Backend(root) is BrowserMinecraftAuthenticationBackend,
            "platform factory preserves Windows WebView2 and selects system browser elsewhere");
        check(OperatingSystem.IsWindows()
                ? PlatformAuthentication.Protector(root) is WindowsAccountDataProtector
                : PlatformAuthentication.Protector(root) is SystemAccountDataProtector,
            "platform factory preserves DPAPI and selects OS-key-backed AES-GCM elsewhere");
        foreach (var invalid in new[] { "", "secret", "00000000-0000-0000-0000-000000000000", "https://example.com" })
            await Rejects<AuthenticationConfigurationException>(() => Task.FromResult(BrowserMinecraftAuthenticationBackend.ValidateClientId(invalid)), check, "invalid/unconfigured public OAuth client ID rejected");
        var app = BrowserMinecraftAuthenticationBackend.CreateApplication(ClientId);
        check(app.AppConfig.ClientId == ClientId && app.AppConfig.RedirectUri == "http://localhost" && app.Authority.Contains("consumers"),
            "MSAL public-client configuration uses personal Microsoft accounts and loopback redirect");
        var initial = Convert.ToBase64String(Encoding.UTF8.GetBytes("{}"));
        var cache = BrowserMinecraftAuthenticationBackend.AttachCache(app, initial);
        check(!(await app.GetAccountsAsync()).Any() && cache() == initial,
            "MSAL empty cache restores in memory without network or extra plaintext files");
        var invalidApp = BrowserMinecraftAuthenticationBackend.CreateApplication(ClientId);
        BrowserMinecraftAuthenticationBackend.AttachCache(invalidApp, "not-base64!");
        await Rejects<FormatException>(async () => await invalidApp.GetAccountsAsync(), check, "malformed MSAL cache rejected before sign-in");
        var browser = new BrowserMinecraftAuthenticationBackend(() => Task.FromResult(""));
        await Rejects<AuthenticationConfigurationException>(() => browser.AuthenticateAsync(new(), null, true, default), check,
            "unconfigured browser login gives actionable error before opening browser/network");
        await Rejects<OperationCanceledException>(() => browser.AuthenticateAsync(new(), null, true, new(true)), check,
            "browser login honors pre-cancellation");
        var accounts = new JsonObject
        {
            [Uuid] = new JsonObject
            {
                [JEProfileSource.KeyName] = JsonSerializer.SerializeToNode(new JEProfile { UUID = Uuid, Username = "FixturePlayer" }),
                [BrowserMinecraftAuthenticationBackend.CacheProperty] = initial
            }
        };
        var original = accounts.ToJsonString();
        check(browser.GetAccounts(accounts).Single().Uuid == Uuid && browser.GetAccounts(accounts).Single().Username == "FixturePlayer",
            "real CmlLib profile parser reads browser accounts with exact Minecraft UUID");
        var without = BrowserMinecraftAuthenticationBackend.WithoutBrowserCache(accounts);
        check(without[Uuid]![BrowserMinecraftAuthenticationBackend.CacheProperty] is null && accounts.ToJsonString() == original,
            "browser-cache adapter keeps credential metadata outside CmlLib parsing without mutating source");
        check(new WindowsMinecraftAuthenticationBackend().GetAccounts(without).Single().Username == "FixturePlayer",
            "legacy CmlLib account JSON remains readable without migration");
        var previous = (JsonObject)accounts.DeepClone();
        previous["other"] = new JsonObject { [BrowserMinecraftAuthenticationBackend.CacheProperty] = "other-fixture-cache" };
        var result = BrowserMinecraftAuthenticationBackend.WithoutBrowserCache(previous);
        BrowserMinecraftAuthenticationBackend.CommitBrowserCache(previous, result, Uuid, "updated-fixture-cache");
        check(result[Uuid]![BrowserMinecraftAuthenticationBackend.CacheProperty]!.GetValue<string>() == "updated-fixture-cache" &&
              result["other"]![BrowserMinecraftAuthenticationBackend.CacheProperty]!.GetValue<string>() == "other-fixture-cache",
            "renewal persists only the selected UUID's refresh cache and preserves other accounts");
        check(previous[Uuid]![BrowserMinecraftAuthenticationBackend.CacheProperty]!.GetValue<string>() == initial,
            "candidate cache changes do not mutate the last committed account snapshot");
        await Rejects<InvalidOperationException>(() => { BrowserMinecraftAuthenticationBackend.CommitBrowserCache(previous,result,Uuid,null); return Task.CompletedTask; },
            check, "successful login cannot publish a profile without a refresh cache");
        foreach (var error in new Exception[] { new MsalUiRequiredException("invalid_grant", "PRIVATE"), new MsalServiceException("invalid_client", "PRIVATE") })
            check(!AuthenticationErrors.ForUser(error, default).ToString().Contains("PRIVATE"), "MSAL errors never expose provider details/tokens");

        var keys = new FixtureKeys();
        var protector = new SystemAccountDataProtector(keys);
        var plain = Encoding.UTF8.GetBytes("fixture-auth-data");
        var first = protector.Protect(plain); var second = protector.Protect(plain);
        check(!first.SequenceEqual(second) && !Encoding.UTF8.GetString(first).Contains("fixture-auth-data") && protector.Unprotect(first).SequenceEqual(plain),
            "Unix AES-GCM round trip uses fresh random nonce and never stores plaintext");
        var damaged = (byte[])first.Clone(); damaged[^1] ^= 1;
        await Rejects<CryptographicException>(() => Task.FromResult(protector.Unprotect(damaged)), check, "modified vault fails authenticated decryption");
        await Rejects<CryptographicException>(() => Task.FromResult(new SystemAccountDataProtector(new FixtureKeys(9)).Unprotect(first)), check,
            "foreign key cannot decrypt account vault");
        await Rejects<CryptographicException>(() => Task.FromResult(protector.Unprotect([1, 2, 3])), check, "truncated/foreign vault rejected");
        keys.Fail = true;
        await Rejects<AccountStorageException>(() => Task.FromResult(protector.Unprotect(first)), check, "unavailable key store does not create replacement key");
        keys.Fail = false;
        var path = Path.Combine(root, "platform-vault", "auth", "accounts.dat");
        var vault = new ProtectedAccountVault(path, protector);
        await vault.SaveAsync(new(accounts, Uuid), default);
        var restored = await new ProtectedAccountVault(path, new SystemAccountDataProtector(keys)).LoadAsync(default);
        check(restored.ActiveAccountId == Uuid && restored.Accounts.ToJsonString() == original,
            "browser refresh cache and selected account commit atomically with existing vault schema");
        var bytes = await File.ReadAllBytesAsync(path);
        keys.Fail = true;
        await Rejects<AccountStorageException>(() => vault.SaveAsync(new(new(), null), default), check, "locked OS key store cannot overwrite existing accounts");
        var preserved = await File.ReadAllBytesAsync(path);
        check(bytes.SequenceEqual(preserved), "key-store failure preserves previous encrypted account file");
        // Opt-in only on a native Linux test host already known to lack libsecret.
        if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("NEXLAUNCHER_TEST_MISSING_SECRET_SERVICE") == "1")
        {
            var nativeDirectory = Path.Combine(root, "missing-system-secret-service");
            var nativePath = Path.Combine(nativeDirectory, "accounts.dat");
            var nativeVault = new ProtectedAccountVault(nativePath,
                new SystemAccountDataProtector(new SystemCredentialKeyStore(nativeDirectory)));
            await Rejects<AccountStorageException>(() => nativeVault.SaveAsync(new(accounts, Uuid), default), check,
                "actual Linux libsecret absence produces safe storage error without plaintext fallback");
            check(!File.Exists(nativePath), "native unavailable secret service publishes no account vault or plaintext tokens");
        }
        if (!OperatingSystem.IsWindows())
            check((File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0,
                "Unix account file is owner-only from creation");

        foreach (var (os, arch, expected, binary) in new[] {
            ("windows", "64", "windows-x64", "javaw.exe"), ("linux", "64", "linux", "java"),
            ("osx", "64", "mac-os", "java"), ("osx", "arm64", "mac-os-arm64", "java") })
        {
            var rules = new RulesEvaluatorContext(new LauncherOSRule(os, arch, "1.0"));
            check(MinecraftJavaManifestResolver.GetOSNameForJava(rules.OS) == expected &&
                  new MinecraftJavaPathResolver(new MinecraftPath(root)).GetJavaBinaryPath(new JavaVersion("java-runtime-delta"), rules).EndsWith(binary),
                "CmlLib selects actual Java platform metadata: " + os + "/" + arch);
        }
        using var version = new JsonVersion(JsonDocument.Parse("""
            {"id":"fixture","type":"release","mainClass":"net.minecraft.client.main.Main","libraries":[],
             "arguments":{"game":[],"jvm":[{"rules":[{"action":"allow","os":{"name":"osx"}}],"value":"-XstartOnFirstThread"}]}}
            """), new JsonVersionParserOptions());
        foreach (var os in new[] { "osx", "linux", "windows" })
        {
            var arguments = new MinecraftProcessBuilder(new RulesEvaluator(), new RulesEvaluatorContext(new LauncherOSRule(os,"64","1.0")),
                new MLaunchOption { Path = new MinecraftPath(root), StartVersion = version, JavaPath = "java", Session = LocalAccountIdentity.CreateSession("FixturePlayer"), NativesDirectory = root }).BuildArguments();
            check(arguments.Contains("-XstartOnFirstThread") == (os == "osx"), "Minecraft's macOS first-thread argument follows official version rules: " + os);
        }
        await Rejects<InvalidOperationException>(() => Task.FromResult(MinecraftRuntimePolicy.RequireExecutableJava(Path.Combine(root, "absent-java"))), check,
            "missing Java gives a platform-neutral actionable error");
        if (!OperatingSystem.IsWindows())
        {
            var java = Path.Combine(root, "non-executable-java");
            await File.WriteAllTextAsync(java, "fixture: never executed");
            File.SetUnixFileMode(java, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await Rejects<InvalidOperationException>(() => Task.FromResult(MinecraftRuntimePolicy.RequireExecutableJava(java)), check,
                "manual non-executable Java is rejected without chmod or execution");
            check(File.GetUnixFileMode(java) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "manual Java permissions remain unchanged");
        }
        check(SafePaths.PathComparison == (OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal),
            "filesystem containment respects case sensitivity on Unix");
    }

    private static async Task Rejects<T>(Func<Task> action, Action<bool, string> check, string message) where T : Exception
    {
        try { await action(); } catch (T) { check(true, message); return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + message);
    }
    private sealed class FixtureKeys(byte value = 42) : ICredentialKeyStore
    {
        public bool Fail;
        public byte[] GetKey(bool create) => Fail ? throw new AccountStorageException("fixture locked keyring") : Enumerable.Repeat(value,32).ToArray();
    }
}
