using CmlLib.Core.Auth;
using NexLauncher.Models;
using NexLauncher.Services;
using NexLauncher.Services.Authentication;

internal static class LocalAccountChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        CheckIdentity(check);
        await CheckLocalIsolation(check);
        await CheckRestart(root, check);
        await CheckMixedAccounts(root, check);
        await CheckMicrosoftPersistence(root, check);
        await CheckMissingMicrosoftSession(check);
        await CheckCrossStoreRemoval(check);
        await CheckFailedWrites(check);
        await CheckCorruptStore(root, check);
    }

    private static void CheckIdentity(Action<bool, string> check)
    {
        check(LocalAccountIdentity.IsValidUsername("Ab_") && LocalAccountIdentity.IsValidUsername("Player_123456789"),
            "local usernames accept ASCII letters, digits and underscores at 3/16-character bounds");
        foreach (var invalid in new string?[] { null, "", "Ab", "Player_1234567890", " Player", "Player ", "A B", "Nick-Name", "Игрок", "éclair", "A\nB", "../player" })
            check(!LocalAccountIdentity.IsValidUsername(invalid), "local username rejects invalid input: " + (invalid is null ? "null" : invalid.Replace("\n", "\\n")));
        check(LocalAccountIdentity.GetUuid("Notch") == "b50ad385829d3141a2167e7d7539ba7f",
            "local UUID matches Java OfflinePlayer:Notch name-based UUID byte ordering");
        check(LocalAccountIdentity.GetUuid("Player") == LocalAccountIdentity.GetUuid("Player") &&
              LocalAccountIdentity.GetUuid("Player") != LocalAccountIdentity.GetUuid("player"),
            "local UUID is deterministic and preserves nickname case");
        var session = LocalAccountIdentity.CreateSession("Notch");
        check(session.Username == "Notch" && session.UUID == "b50ad385829d3141a2167e7d7539ba7f" && session.UserType == "legacy",
            "local identity creates an explicitly local session with saved username and stable UUID");
        check(session.AccessToken == "access_token" && session.ClientToken is null && session.Xuid is null,
            "local session uses only CmlLib offline placeholder fields, with no Microsoft credentials");
        try { LocalAccountIdentity.GetUuid("bad name"); throw new InvalidOperationException("Invalid nickname accepted."); }
        catch (ArgumentException) { check(true, "local UUID helper validates names before deriving identity"); }
    }

    private static async Task CheckLocalIsolation(Action<bool, string> check)
    {
        var microsoft = new CountingMicrosoft();
        var storage = new MemoryLocalStore();
        var service = new AccountService(microsoft, storage);
        var first = await service.CreateLocalAccountAsync("Solo_Player", default);
        check(first.Type == AccountType.Local && service.ActiveAccountId == first.Id && service.Accounts.Count == 1,
            "creating a local profile selects it without prior initialization");
        var session = await service.RestoreAsync(default);
        check(session?.Username == "Solo_Player" && session.UUID == first.Uuid,
            "local restore returns the stored identity without OAuth refresh");
        await service.SelectAccountAsync(first.Id, default);
        var repeated = await service.CreateLocalAccountAsync("Solo_Player", default);
        check(repeated.Id == first.Id && service.Accounts.Count == 1,
            "creating the same exact local nickname selects the existing account without duplicates");
        var caseVariant = await service.CreateLocalAccountAsync("solo_player", default);
        check(caseVariant.Id != first.Id && caseVariant.Uuid != first.Uuid && service.Accounts.Count == 2,
            "case-distinct local nicknames retain distinct offline identities");
        await service.RemoveAccountAsync(first.Id, default);
        check(service.ActiveAccountId == caseVariant.Id && service.Accounts.Single().Id == caseVariant.Id,
            "removing another local profile keeps the active one");
        await Rejects<InvalidOperationException>(() => service.SelectAccountAsync("missing-profile", default), check,
            "unknown local account selection is rejected");
        await service.SignOutAsync();
        check(service.Accounts.Count == 0 && service.ActiveAccountId is null && await service.RestoreAsync(default) is null,
            "removing the last local account clears the active identity and launch session");
        check(microsoft.TotalInteractions == 0,
            "cold local creation, selection, restore and removal never access Microsoft provider methods or cached properties");
        await Rejects<ArgumentException>(() => service.CreateLocalAccountAsync("bad name", default), check,
            "local account creation rejects an invalid nickname");
        await Rejects<OperationCanceledException>(() => service.CreateLocalAccountAsync("Cancelled", new CancellationToken(true)), check,
            "cancelled local account creation does not save a profile");
        check(service.Accounts.Count == 0 && storage.Snapshot.LocalAccounts.Count == 0 && microsoft.TotalInteractions == 0,
            "invalid or cancelled local creation leaves storage and Microsoft backend untouched");
    }

    private static async Task CheckRestart(string root, Action<bool, string> check)
    {
        var path = Path.Combine(root, "local-account-restart", "profiles.json");
        var firstService = new AccountService(new CountingMicrosoft(), new LocalAccountStore(path));
        var first = await firstService.CreateLocalAccountAsync("Notch", default);
        await firstService.CreateLocalAccountAsync("Steve", default);
        await firstService.SelectAccountAsync(first.Id, default);
        var unavailableMicrosoft = new CountingMicrosoft { FailInitialization = true };
        var restarted = new AccountService(unavailableMicrosoft, new LocalAccountStore(path));
        await restarted.InitializeAsync(default);
        check(restarted.Accounts.Count == 2 && restarted.ActiveAccountId == first.Id && restarted.PlayerName == "Notch",
            "local profiles and selected nickname survive a new account service and disk-store instance");
        check(!string.IsNullOrWhiteSpace(restarted.MicrosoftAvailabilityWarning),
            "inaccessible Microsoft storage is reported independently of local accounts");
        var interactions = unavailableMicrosoft.TotalInteractions;
        var session = await restarted.RestoreAsync(default);
        check(session?.UUID == "b50ad385829d3141a2167e7d7539ba7f" && unavailableMicrosoft.TotalInteractions == interactions,
            "restored local profile remains usable when Microsoft vault initialization failed");
        var json = await File.ReadAllTextAsync(path);
        check(json.Contains("Notch") && json.Contains("Steve") && !json.Contains("access_token") && !json.Contains("refresh_token") && !json.Contains("ClientToken"),
            "public local-account storage contains nicknames and selection but no session credentials");
    }

    private static async Task CheckMixedAccounts(string root, Action<bool, string> check)
    {
        var microsoft = new CountingMicrosoft();
        var path = Path.Combine(root, "mixed-accounts", "profiles.json");
        var service = new AccountService(microsoft, new LocalAccountStore(path));
        await service.InitializeAsync(default);
        var microsoftSession = await service.SignInAsync(default);
        var onlineAccount = service.Accounts.Single();
        check(onlineAccount.Type == AccountType.Microsoft && service.ActiveAccountId == onlineAccount.Id,
            "Microsoft sign-in enters the common account list with an explicit provider type");
        var local = await service.CreateLocalAccountAsync(onlineAccount.Username, default);
        check(service.Accounts.Count == 2 && local.Id != onlineAccount.Id && service.ActiveAccountId == local.Id,
            "Microsoft and local profiles may share a display nickname without sharing identity");
        await service.SelectAccountAsync(onlineAccount.Id, default);
        var refreshedMicrosoft = await service.RestoreAsync(default);
        check(refreshedMicrosoft is not null && refreshedMicrosoft.UUID == microsoftSession.UUID && refreshedMicrosoft.AccessToken == CountingMicrosoft.FixtureToken && microsoft.RestoreCalls == 1,
            "switching to Microsoft delegates refresh and returns its authenticated session");
        var interactions = microsoft.TotalInteractions;
        await service.SelectAccountAsync(local.Id, default);
        var restoredLocal = await service.RestoreAsync(default);
        check(restoredLocal?.UUID == local.Uuid && restoredLocal.UserType == "legacy" && microsoft.TotalInteractions == interactions,
            "switching back to local and restoring it makes no Microsoft calls");
        var repeated = await service.CreateLocalAccountAsync(local.Username, default);
        check(service.Accounts.Count == 2 && repeated.Id == local.Id, "local duplicate prevention also works in the mixed account list");
        var json = await File.ReadAllTextAsync(path);
        check(!json.Contains(CountingMicrosoft.FixtureToken) && !json.Contains("AccessToken") && !json.Contains("refresh_token"),
            "common selection metadata never writes Microsoft session tokens into public JSON");
        await service.RemoveAccountAsync(onlineAccount.Id, default);
        check(service.Accounts.Single().Id == local.Id && service.ActiveAccountId == local.Id && microsoft.RemoveCalls == 1,
            "removing a Microsoft profile delegates secure deletion and preserves the active local account");
        var afterRemoval = new AccountService(microsoft, new LocalAccountStore(path));
        await afterRemoval.InitializeAsync(default);
        check(afterRemoval.Accounts.Single().Id == local.Id && afterRemoval.ActiveAccountId == local.Id,
            "mixed-account removal and local selection survive restart");
    }

    private static async Task CheckMicrosoftPersistence(string root, Action<bool, string> check)
    {
        var microsoft = new CountingMicrosoft();
        await microsoft.SignInAsync(default);
        microsoft.NextId = "aaaaaaaa222233334444555555555555";
        var priorSession = await microsoft.SignInAsync(default);
        var path = Path.Combine(root, "microsoft-selection-migration", "profiles.json");
        var migrated = new AccountService(microsoft, new LocalAccountStore(path));
        await migrated.InitializeAsync(default);
        check(migrated.Accounts.Count == 2 && migrated.ActiveAccountId == priorSession.UUID && microsoft.SignInCalls == 2 && microsoft.RestoreCalls == 0 && microsoft.SelectCalls == 0,
            "existing Microsoft selection migrates to public metadata without interactive login, refresh or vault-selection write");
        var persisted = await new LocalAccountStore(path).LoadAsync(default);
        check(persisted.ActiveAccountType == AccountType.Microsoft && persisted.ActiveAccountId == priorSession.UUID && persisted.LocalAccounts.Count == 0,
            "Microsoft-only migration persists provider type and active account reference");
        var local = await migrated.CreateLocalAccountAsync("LocalBefore", default);
        await migrated.SelectAccountAsync(priorSession.UUID ?? throw new InvalidOperationException("Missing fixture UUID"), default);
        var restarted = new AccountService(microsoft, new LocalAccountStore(path));
        await restarted.InitializeAsync(default);
        check(restarted.ActiveAccountId == priorSession.UUID && restarted.Accounts.Count == 3 && restarted.Accounts.Any(x => x.Id == local.Id),
            "explicit Microsoft selection survives restart alongside saved local profiles");
        var restored = await restarted.RestoreAsync(default);
        check(restored is not null && restored.UUID == priorSession.UUID && restored.UserType == "msa",
            "restarted Microsoft selection refreshes its own session instead of a local or earlier Microsoft profile");
    }

    private static async Task CheckMissingMicrosoftSession(Action<bool, string> check)
    {
        var microsoft = new CountingMicrosoft();
        var storage = new MemoryLocalStore();
        var service = new AccountService(microsoft, storage);
        var online = await service.SignInAsync(default);
        var local = await service.CreateLocalAccountAsync("AvailableLocal", default);
        await service.SelectAccountAsync(online.UUID ?? throw new InvalidOperationException("Missing fixture UUID"), default);
        microsoft.ReturnNullSession = true;
        await Rejects<InvalidOperationException>(() => service.RestoreAsync(default), check,
            "an explicitly selected Microsoft account with no restored session fails without offline fallback");
        check(service.ActiveAccountId == online.UUID && storage.Snapshot.ActiveAccountType == AccountType.Microsoft && service.Accounts.Count == 2,
            "a missing Microsoft session preserves explicit Microsoft selection despite an available local profile");
        await microsoft.RemoveAccountAsync(online.UUID, default);
        await Rejects<InvalidOperationException>(() => service.RestoreAsync(default), check,
            "missing Microsoft credentials also fail without returning the saved local session");
        check(service.ActiveAccountId is null && service.Accounts.Single().Id == local.Id,
            "missing Microsoft credentials leave the local account available but unselected");
        var interactions = microsoft.TotalInteractions;
        await service.SelectAccountAsync(local.Id, default);
        var localSession = await service.RestoreAsync(default);
        check(localSession is not null && localSession.UUID == local.Uuid && microsoft.TotalInteractions == interactions,
            "local play after Microsoft failure requires an explicit local selection and no extra Microsoft request");
    }

    private static async Task CheckCrossStoreRemoval(Action<bool, string> check)
    {
        var microsoft = new CountingMicrosoft();
        var storage = new MemoryLocalStore();
        var service = new AccountService(microsoft, storage);
        var online = await service.SignInAsync(default);
        var local = await service.CreateLocalAccountAsync("KeptLocal", default);
        await service.SelectAccountAsync(online.UUID ?? throw new InvalidOperationException("Missing fixture UUID"), default);
        storage.FailNextSave = true;
        await Rejects<AccountStorageException>(() => service.RemoveAccountAsync(online.UUID, default), check,
            "Microsoft credential removal reports a later public-selection save failure");
        check(microsoft.RemoveCalls == 1 && service.Accounts.Single().Id == local.Id && service.ActiveAccountId is null,
            "public save failure cannot resurrect already-deleted Microsoft credentials or publish a ghost active account");
        check(storage.Snapshot.ActiveAccountType == AccountType.Microsoft && storage.Snapshot.ActiveAccountId == online.UUID,
            "failed cross-store selection write preserves the prior public file for explicit recovery");
        var restarted = new AccountService(microsoft, storage);
        await restarted.InitializeAsync(default);
        check(restarted.Accounts.Single().Id == local.Id && restarted.ActiveAccountId is null,
            "restart after partial account removal does not recreate Microsoft credentials or silently choose local");
        await restarted.SelectAccountAsync(local.Id, default);
        check(storage.Snapshot.ActiveAccountId == local.Id && storage.Snapshot.ActiveAccountType == AccountType.Local,
            "explicit account selection repairs the public pointer after a partial removal");

        // Once secure deletion committed, late cancellation must not interrupt the public selection commit.
        var cancellationMicrosoft = new CountingMicrosoft();
        var cancellationStore = new MemoryLocalStore();
        var cancellationService = new AccountService(cancellationMicrosoft, cancellationStore);
        var cancellationOnline = await cancellationService.SignInAsync(default);
        var cancellationLocal = await cancellationService.CreateLocalAccountAsync("AfterCancel", default);
        await cancellationService.SelectAccountAsync(cancellationOnline.UUID ?? throw new InvalidOperationException("Missing fixture UUID"), default);
        using var cancellation = new CancellationTokenSource();
        cancellationMicrosoft.CancelAfterRemoval = cancellation;
        await cancellationService.RemoveAccountAsync(cancellationOnline.UUID, cancellation.Token);
        check(cancellation.IsCancellationRequested && cancellationService.ActiveAccountId == cancellationLocal.Id && cancellationStore.Snapshot.ActiveAccountId == cancellationLocal.Id,
            "late cancellation after secure deletion still completes the public account-selection commit");
    }
    private static async Task CheckFailedWrites(Action<bool, string> check)
    {
        var storage = new MemoryLocalStore();
        var microsoft = new CountingMicrosoft();
        var service = new AccountService(microsoft, storage);
        var first = await service.CreateLocalAccountAsync("First", default);
        var second = await service.CreateLocalAccountAsync("Second", default);
        storage.FailNextSave = true;
        await Rejects<AccountStorageException>(() => service.CreateLocalAccountAsync("Third", default), check,
            "local creation reports a failed public-store write");
        check(service.Accounts.Count == 2 && service.ActiveAccountId == second.Id && storage.Snapshot.LocalAccounts.Count == 2,
            "failed local creation does not publish or persist a candidate account");
        storage.FailNextSave = true;
        await Rejects<AccountStorageException>(() => service.SelectAccountAsync(first.Id, default), check,
            "local selection reports a failed public-store write");
        check(service.ActiveAccountId == second.Id && storage.Snapshot.ActiveAccountId == second.Id,
            "failed local selection preserves the previous active identity");
        storage.FailNextSave = true;
        await Rejects<AccountStorageException>(() => service.RemoveAccountAsync(second.Id, default), check,
            "local removal reports a failed public-store write");
        check(service.Accounts.Count == 2 && service.ActiveAccountId == second.Id && storage.Snapshot.LocalAccounts.Count == 2 && microsoft.TotalInteractions == 0,
            "failed local removal preserves accounts and never reaches Microsoft storage");
    }

    private static async Task CheckCorruptStore(string root, Action<bool, string> check)
    {
        var directory = Path.Combine(root, "local-account-corrupt");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "profiles.json");
        await File.WriteAllTextAsync(path, "{broken");
        var service = new AccountService(new CountingMicrosoft(), new LocalAccountStore(path));
        await Rejects<AccountStorageException>(() => service.CreateLocalAccountAsync("Player", default), check,
            "corrupt local-profile storage is not silently replaced during creation");
        check(await File.ReadAllTextAsync(path) == "{broken", "corrupt local-account JSON remains untouched for recovery");
    }

    private static async Task Rejects<T>(Func<Task> action, Action<bool, string> check, string description) where T : Exception
    {
        try { await action(); }
        catch (T) { check(true, description); return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + description);
    }

    private sealed class MemoryLocalStore : ILocalAccountStore
    {
        public LocalAccountSnapshot Snapshot { get; private set; } = LocalAccountSnapshot.Empty();
        public bool FailNextSave { get; set; }
        public Task<LocalAccountSnapshot> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Copy(Snapshot));
        }
        public Task SaveAsync(LocalAccountSnapshot snapshot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailNextSave) { FailNextSave = false; throw new AccountStorageException("Fixture storage failure."); }
            Snapshot = Copy(snapshot);
            return Task.CompletedTask;
        }
        private static LocalAccountSnapshot Copy(LocalAccountSnapshot value) =>
            new(value.LocalAccounts.ToArray(), value.ActiveAccountType, value.ActiveAccountId);
    }

    private sealed class CountingMicrosoft : IMicrosoftAccountService
    {
        public const string FixtureToken = "LOCAL-CHECK-MICROSOFT-SECRET-FIXTURE";
        private const string ProfileId = "11111111222233334444555555555555";
        private readonly List<LauncherAccount> _accounts = new();
        private string? _active;
        public int TotalInteractions { get; private set; }
        public int RestoreCalls { get; private set; }
        public int SignInCalls { get; private set; }
        public int SelectCalls { get; private set; }
        public string NextId { get; set; } = ProfileId;
        public bool ReturnNullSession { get; set; }
        public CancellationTokenSource? CancelAfterRemoval { get; set; }
        public int RemoveCalls { get; private set; }
        public bool FailInitialization { get; set; }
        public IReadOnlyList<LauncherAccount> Accounts { get { TotalInteractions++; return _accounts.ToArray(); } }
        public string? ActiveAccountId { get { TotalInteractions++; return _active; } }
        public string? PlayerName { get { TotalInteractions++; return _accounts.FirstOrDefault(x => x.Id == _active)?.Username; } }
        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); TotalInteractions++;
            if (FailInitialization) throw new AccountStorageException("Fixture inaccessible vault.");
            return Task.CompletedTask;
        }
        public Task<MSession> SignInAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); TotalInteractions++;
            SignInCalls++;
            if (_accounts.All(x => x.Id != NextId)) _accounts.Add(new LauncherAccount(NextId, "MsPlayer", NextId));
            _active = NextId;
            return Task.FromResult(Session());
        }
        public Task<MSession?> RestoreAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); TotalInteractions++; RestoreCalls++;
            return Task.FromResult<MSession?>(_active is null || ReturnNullSession ? null : Session());
        }
        public Task SelectAccountAsync(string id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); TotalInteractions++;
            SelectCalls++;
            if (_accounts.All(x => x.Id != id)) throw new InvalidOperationException("Fixture account not found.");
            _active = id;
            return Task.CompletedTask;
        }
        public Task RemoveAccountAsync(string id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); TotalInteractions++; RemoveCalls++;
            _accounts.RemoveAll(x => x.Id == id);
            if (_active == id) _active = _accounts.FirstOrDefault()?.Id;
            CancelAfterRemoval?.Cancel();
            return Task.CompletedTask;
        }
        public Task SignOutAsync() => _active is null ? Task.CompletedTask : RemoveAccountAsync(_active, default);
        private MSession Session() => new("MsPlayer", FixtureToken, _active!) { UserType = "msa" };
    }
}
