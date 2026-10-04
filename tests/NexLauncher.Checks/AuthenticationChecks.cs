using System.Text;
using System.Text.Json.Nodes;
using CmlLib.Core.Auth;
using NexLauncher.Models;
using NexLauncher.Services;
using NexLauncher.Services.Authentication;

internal static class AuthenticationChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var directory = Path.Combine(root, "authentication");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "accounts.dat");
        var vault = new ProtectedAccountVault(path, new WindowsAccountDataProtector());
        var snapshot = new AccountVaultSnapshot(
            new JsonObject { ["alpha"] = new JsonObject { ["username"] = "Alice", ["token"] = "TEST-SECRET-NOT-A-REAL-TOKEN" } },
            "alpha");
        await vault.SaveAsync(snapshot, default);
        var encrypted = await File.ReadAllBytesAsync(path);
        check(!Encoding.UTF8.GetString(encrypted).Contains("TEST-SECRET") &&
              !Encoding.UTF8.GetString(encrypted).Contains("Alice"), "DPAPI vault has no plaintext token or profile");
        var restored = await new ProtectedAccountVault(path, new WindowsAccountDataProtector()).LoadAsync(default);
        check(restored.ActiveAccountId == "alpha" &&
              restored.Accounts["alpha"]!["token"]!.GetValue<string>() == "TEST-SECRET-NOT-A-REAL-TOKEN",
            "DPAPI vault and selected account survive a new store instance");

        var inaccessible = new ProtectedAccountVault(path, new RejectingProtector());
        await Rejects<AccountStorageException>(() => inaccessible.LoadAsync(default), check, "foreign or inaccessible protection fails safely");
        await Rejects<AccountStorageException>(() => inaccessible.SaveAsync(snapshot, default), check, "foreign vault cannot be overwritten");
        check(Enumerable.SequenceEqual(encrypted, await File.ReadAllBytesAsync(path)), "failed decryption leaves original bytes intact");
        await Rejects<OperationCanceledException>(() => vault.SaveAsync(snapshot, new CancellationToken(true)),
            check, "cancelled vault write is rejected");
        check(Enumerable.SequenceEqual(encrypted, await File.ReadAllBytesAsync(path)), "cancelled vault write preserves original bytes");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        await Rejects<AccountStorageException>(() => vault.SaveAsync(snapshot, default), check, "corrupt vault cannot be overwritten");
        check((await File.ReadAllBytesAsync(path)).SequenceEqual(new byte[] { 1, 2, 3, 4 }), "corrupt encrypted file is preserved");

        var backend = new FakeBackend();
        var memory = new MemoryVault();
        var service = new MicrosoftAccountService(backend, memory);
        await service.InitializeAsync(default);
        check(service.Accounts.Count == 0 && service.ActiveAccountId is null, "account service starts empty");
        backend.NextId = "alpha";
        var first = await service.SignInAsync(default);
        backend.NextId = "beta";
        await service.SignInAsync(default);
        check(service.Accounts.Count == 2 && service.PlayerName == "User-beta" && first.AccessToken == "fake-session",
            "multiple accounts preserve a real backend session result");
        await service.SelectAccountAsync("alpha", default);
        var reloaded = new MicrosoftAccountService(backend, memory);
        await reloaded.InitializeAsync(default);
        check(reloaded.ActiveAccountId == "alpha" && reloaded.Accounts.Count == 2, "selected account restores from persisted state");
        await reloaded.RestoreAsync(default);
        check(backend.LastAccountId == "alpha" && !backend.LastInteractive, "restore uses selected account and silent backend flow");
        var skinSession = await reloaded.GetSessionForAccountAsync("beta", default);
        check(skinSession.UUID == "beta" && backend.LastAccountId == "beta" && !backend.LastInteractive && reloaded.ActiveAccountId == "alpha",
            "skin session refresh targets a saved non-active Microsoft account without changing launch selection");
        check((await memory.LoadAsync(default)).ActiveAccountId == "alpha", "skin session refresh preserves active account in protected vault metadata");
        backend.NextId = "alpha";
        await reloaded.SignInAsync(default);
        check(reloaded.Accounts.Count == 2, "repeat sign-in replaces the same UUID without duplicate accounts");
        memory.FailNextSave = true;
        await Rejects<AccountStorageException>(() => reloaded.RemoveAccountAsync("alpha", default),
            check, "failed account removal is reported");
        check(reloaded.ActiveAccountId == "alpha" && reloaded.Accounts.Count == 2 &&
              (await memory.LoadAsync(default)).Accounts.Count == 2, "failed save rolls back UI selection and accounts");
        backend.Cancel = true;
        await Rejects<OperationCanceledException>(() => reloaded.SignInAsync(default), check, "cancelled login propagates cancellation");
        check(reloaded.Accounts.Count == 2 && (await memory.LoadAsync(default)).Accounts.Count == 2,
            "cancelled login does not persist candidate credentials");
        backend.Cancel = false;
        await reloaded.SignOutAsync();
        check(reloaded.Accounts.Count == 1 && reloaded.ActiveAccountId == "beta", "logout removes only active account and selects remaining account");
        await reloaded.RemoveAccountAsync("beta", default);
        check(reloaded.Accounts.Count == 0 && await reloaded.RestoreAsync(default) is null, "last-account removal leaves no launch session");
        check(!AuthenticationErrors.ForUser(new Exception("access_token=PRIVATE"), default).ToString().Contains("PRIVATE"),
            "auth error translation suppresses credential-bearing details");
        check(!AuthenticationErrors.ForUser(new HttpRequestException("Bearer PRIVATE"), default).Message.Contains("PRIVATE"),
            "network auth error never exposes response details");
    }

    private static async Task Rejects<T>(Func<Task> action, Action<bool, string> check, string description) where T : Exception
    {
        try { await action(); }
        catch (T) { check(true, description); return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + description);
    }

    private sealed class RejectingProtector : IAccountDataProtector
    {
        public byte[] Protect(byte[] plaintext) => throw new System.Security.Cryptography.CryptographicException("PRIVATE");
        public byte[] Unprotect(byte[] ciphertext) => throw new System.Security.Cryptography.CryptographicException("PRIVATE");
    }

    private sealed class MemoryVault : IAccountVault
    {
        private AccountVaultSnapshot _state = AccountVaultSnapshot.Empty();
        public bool FailNextSave { get; set; }
        public Task<AccountVaultSnapshot> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(_state.Copy());
        public Task SaveAsync(AccountVaultSnapshot snapshot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailNextSave)
            {
                FailNextSave = false;
                throw new AccountStorageException("Test storage failure.");
            }
            _state = snapshot.Copy();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeBackend : IMinecraftAuthenticationBackend
    {
        public string NextId { get; set; } = "alpha";
        public string? LastAccountId { get; private set; }
        public bool LastInteractive { get; private set; }
        public bool Cancel { get; set; }
        public IReadOnlyList<LauncherAccount> GetAccounts(JsonObject storedAccounts) => storedAccounts
            .Select(item => new LauncherAccount(item.Key, item.Value!["username"]!.GetValue<string>(), item.Key)).ToArray();
        public Task<MinecraftAuthenticationResult> AuthenticateAsync(
            JsonObject storedAccounts, string? accountId, bool interactive, CancellationToken cancellationToken)
        {
            LastAccountId = accountId;
            LastInteractive = interactive;
            var id = accountId ?? NextId;
            storedAccounts[id] = new JsonObject { ["username"] = "User-" + id, ["token"] = "candidate-secret" };
            if (Cancel) throw new OperationCanceledException();
            return Task.FromResult(new MinecraftAuthenticationResult(new MSession
            {
                Username = "User-" + id,
                UUID = id,
                UserType = "msa",
                AccessToken = "fake-session"
            }, storedAccounts));
        }
    }
}
