using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CmlLib.Core.Auth;
using NexLauncher.Models;
using NexLauncher.Services.Authentication;

namespace NexLauncher.Services;

/// <summary>
/// Combines public local identities with the existing protected Microsoft provider.
/// Local operations never access the provider; only cached public Microsoft profiles are used.
/// </summary>
public sealed class AccountService : IAccountService
{
    private readonly IMicrosoftAccountService _microsoft;
    private readonly ILocalAccountStore _localStore;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private LocalAccountSnapshot _snapshot = LocalAccountSnapshot.Empty();
    private IReadOnlyList<LauncherAccount> _microsoftAccounts = Array.Empty<LauncherAccount>();
    private string? _microsoftActiveAccountId;
    private bool _localInitialized;

    public AccountService(string dataDirectory) : this(new MicrosoftAccountService(dataDirectory),
        new LocalAccountStore(Path.Combine(dataDirectory, "auth", "profiles.json")))
    {
    }

    public AccountService(IMicrosoftAccountService microsoft, ILocalAccountStore localStore)
    {
        _microsoft = microsoft;
        _localStore = localStore;
    }

    public IReadOnlyList<LauncherAccount> Accounts { get; private set; } = Array.Empty<LauncherAccount>();
    public string? ActiveAccountId => ActiveAccount?.Id;
    public string? PlayerName => ActiveAccount?.Username;
    public string? MicrosoftAvailabilityWarning { get; private set; }
    private LauncherAccount? ActiveAccount => Accounts.FirstOrDefault(account =>
        account.Id == _snapshot.ActiveAccountId && account.Type == _snapshot.ActiveAccountType);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _operation.WaitAsync(cancellationToken);
        try
        {
            await EnsureLocalInitializedAsync(cancellationToken);
            try { await LoadMicrosoftAsync(cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                // A broken or foreign encrypted vault must not disable local play or be overwritten.
                MicrosoftAvailabilityWarning = "Сохранённые аккаунты Microsoft недоступны. Проверь защищённое хранилище auth/accounts.dat. Локальные аккаунты доступны; защищённый файл не изменён.";
            }
            // First-run migration of the previous Microsoft-only selection. No vault write is needed.
            if (_snapshot.ActiveAccountId is null)
            {
                var selected = _microsoftAccounts.FirstOrDefault(account => account.Id == _microsoftActiveAccountId)
                    ?? Accounts.FirstOrDefault();
                if (selected is not null)
                    await CommitAsync(Select(_snapshot, selected), cancellationToken);
            }
        }
        finally { _operation.Release(); }
    }

    public async Task<LauncherAccount> CreateLocalAccountAsync(string username, CancellationToken cancellationToken)
    {
        // Validate before any disk access, even when called outside the UI.
        var profile = LocalAccountIdentity.CreateProfile(username);
        await _operation.WaitAsync(cancellationToken);
        try
        {
            await EnsureLocalInitializedAsync(cancellationToken);
            var existing = _snapshot.LocalAccounts.FirstOrDefault(account => account.Id == profile.Id);
            var locals = existing is null ? _snapshot.LocalAccounts.Append(profile).ToArray() : _snapshot.LocalAccounts;
            await CommitAsync(new LocalAccountSnapshot(locals, AccountType.Local, profile.Id), cancellationToken);
            return existing ?? profile;
        }
        finally { _operation.Release(); }
    }

    public async Task<MSession> SignInAsync(CancellationToken cancellationToken)
    {
        await _operation.WaitAsync(cancellationToken);
        try
        {
            await EnsureLocalInitializedAsync(cancellationToken);
            await LoadMicrosoftAsync(cancellationToken);
            var session = await _microsoft.SignInAsync(cancellationToken);
            RefreshMicrosoftProfiles();
            var account = _microsoftAccounts.FirstOrDefault(item => item.Id == _microsoftActiveAccountId)
                ?? throw new InvalidOperationException("Microsoft не вернул сохранённый профиль Minecraft.");
            // Credentials are already protected by the provider. Only the active public reference is stored here.
            await CommitAsync(Select(_snapshot, account), cancellationToken);
            return session;
        }
        finally { _operation.Release(); }
    }

    public async Task<MSession?> RestoreAsync(CancellationToken cancellationToken)
    {
        await _operation.WaitAsync(cancellationToken);
        try
        {
            await EnsureLocalInitializedAsync(cancellationToken);
            if (_snapshot.ActiveAccountType == AccountType.Local)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ActiveAccount is { } local ? LocalAccountIdentity.CreateSession(local.Username) : null;
            }
            if (_snapshot.ActiveAccountId is null) return null;
            await LoadMicrosoftAsync(cancellationToken);
            var selected = RequireAccount(_snapshot.ActiveAccountId);
            // The old vault's selection can differ while a local profile is active.
            await _microsoft.SelectAccountAsync(selected.Id, cancellationToken);
            var session = await _microsoft.RestoreAsync(cancellationToken)
                ?? throw new InvalidOperationException("Сохранённая сессия Microsoft недоступна. Войди через Microsoft ещё раз; локальный режим не включается автоматически.");
            RefreshMicrosoftProfiles();
            return session;
        }
        finally { _operation.Release(); }
    }

    public async Task<MSession> GetSessionForAccountAsync(string id, CancellationToken cancellationToken)
    {
        await _operation.WaitAsync(cancellationToken);
        try
        {
            await EnsureLocalInitializedAsync(cancellationToken);
            if (RequireAccount(id).Type != AccountType.Microsoft)
                throw new InvalidOperationException("Локальный аккаунт не имеет Microsoft credentials.");
            await LoadMicrosoftAsync(cancellationToken);
            var session = await _microsoft.GetSessionForAccountAsync(id, cancellationToken);
            RefreshMicrosoftProfiles(); return session;
        }
        finally { _operation.Release(); }
    }

    public async Task SelectAccountAsync(string id, CancellationToken cancellationToken)
    {
        await _operation.WaitAsync(cancellationToken);
        try
        {
            await EnsureLocalInitializedAsync(cancellationToken);
            var account = RequireAccount(id);
            if (account.Id != _snapshot.ActiveAccountId || account.Type != _snapshot.ActiveAccountType)
                await CommitAsync(Select(_snapshot, account), cancellationToken);
        }
        finally { _operation.Release(); }
    }

    public async Task RemoveAccountAsync(string id, CancellationToken cancellationToken)
    {
        await _operation.WaitAsync(cancellationToken);
        try
        {
            await EnsureLocalInitializedAsync(cancellationToken);
            await RemoveCoreAsync(RequireAccount(id), cancellationToken);
        }
        finally { _operation.Release(); }
    }

    public async Task SignOutAsync()
    {
        await _operation.WaitAsync();
        try
        {
            await EnsureLocalInitializedAsync(CancellationToken.None);
            if (ActiveAccount is { } account)
                await RemoveCoreAsync(account, CancellationToken.None);
        }
        finally { _operation.Release(); }
    }

    private async Task EnsureLocalInitializedAsync(CancellationToken cancellationToken)
    {
        if (_localInitialized) return;
        var loaded = await _localStore.LoadAsync(cancellationToken);
        _snapshot = loaded.Copy();
        _localInitialized = true;
        PublishProfiles();
    }

    private async Task LoadMicrosoftAsync(CancellationToken cancellationToken)
    {
        await _microsoft.InitializeAsync(cancellationToken);
        RefreshMicrosoftProfiles();
        MicrosoftAvailabilityWarning = null;
    }

    private void RefreshMicrosoftProfiles()
    {
        _microsoftAccounts = Array.AsReadOnly(_microsoft.Accounts.ToArray());
        _microsoftActiveAccountId = _microsoft.ActiveAccountId;
        PublishProfiles();
    }

    private void PublishProfiles() =>
        Accounts = Array.AsReadOnly(_microsoftAccounts.Concat(_snapshot.LocalAccounts).ToArray());

    private async Task CommitAsync(LocalAccountSnapshot candidate, CancellationToken cancellationToken)
    {
        await _localStore.SaveAsync(candidate, cancellationToken);
        _snapshot = candidate.Copy();
        PublishProfiles();
    }

    private async Task RemoveCoreAsync(LauncherAccount account, CancellationToken cancellationToken)
    {
        var locals = account.Type == AccountType.Local
            ? _snapshot.LocalAccounts.Where(item => item.Id != account.Id).ToArray()
            : _snapshot.LocalAccounts;
        var candidate = _snapshot with { LocalAccounts = locals };
        if (account.Id == candidate.ActiveAccountId && account.Type == candidate.ActiveAccountType)
            candidate = Select(candidate, Accounts.FirstOrDefault(item => item.Id != account.Id));

        if (account.Type == AccountType.Local)
        {
            await CommitAsync(candidate, cancellationToken);
            return;
        }

        // Delete credentials through the existing encrypted provider only. If public persistence fails,
        // the removed account remains removed and the previous selection is simply unavailable.
        await _microsoft.RemoveAccountAsync(account.Id, cancellationToken);
        RefreshMicrosoftProfiles();
        try
        {
            // Credential deletion has committed. Complete the public selection even if Cancel
            // is pressed now; reintroducing removed Microsoft credentials is not a rollback option.
            await CommitAsync(candidate, CancellationToken.None);
        }
        catch (Exception)
        {
            throw new AccountStorageException("Аккаунт Microsoft удалён. Не удалось сохранить выбор следующего аккаунта в auth/profiles.json. Проверь доступ к файлу и выбери аккаунт снова.");
        }
    }

    private LauncherAccount RequireAccount(string id) => Accounts.FirstOrDefault(account => account.Id == id)
        ?? throw new InvalidOperationException("Выбранный аккаунт больше не сохранён. Обнови список аккаунтов.");

    private static LocalAccountSnapshot Select(LocalAccountSnapshot snapshot, LauncherAccount? account) =>
        snapshot with { ActiveAccountType = account?.Type, ActiveAccountId = account?.Id };
}


