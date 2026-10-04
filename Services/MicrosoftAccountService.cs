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

/// <summary>Serializes account operations and publishes changes only after encrypted storage commits.</summary>
public sealed class MicrosoftAccountService : IMicrosoftAccountService
{
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly IMinecraftAuthenticationBackend _backend;
    private readonly IAccountVault _vault;
    private AccountVaultSnapshot _snapshot = AccountVaultSnapshot.Empty();
    private bool _initialized;

    public MicrosoftAccountService(string dataDirectory) : this(
        new WindowsMinecraftAuthenticationBackend(),
        new ProtectedAccountVault(Path.Combine(dataDirectory, "auth", "accounts.dat"), new WindowsAccountDataProtector()))
    {
    }

    public MicrosoftAccountService(IMinecraftAuthenticationBackend backend, IAccountVault vault)
    {
        _backend = backend;
        _vault = vault;
    }

    public IReadOnlyList<LauncherAccount> Accounts { get; private set; } = Array.Empty<LauncherAccount>();
    public string? ActiveAccountId => _snapshot.ActiveAccountId;
    public string? PlayerName => Accounts.FirstOrDefault(account => account.Id == ActiveAccountId)?.Username;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _operation.WaitAsync(cancellationToken);
        try { await EnsureInitializedAsync(cancellationToken); }
        finally { _operation.Release(); }
    }

    public async Task<MSession> SignInAsync(CancellationToken cancellationToken)
    {
        await _operation.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            var result = await _backend.AuthenticateAsync(_snapshot.Copy().Accounts, null, true, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await CommitAsync(new AccountVaultSnapshot(result.Accounts, result.Session.UUID), cancellationToken);
            return result.Session;
        }
        finally { _operation.Release(); }
    }

    public async Task<MSession?> RestoreAsync(CancellationToken cancellationToken)
    {
        await _operation.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            if (ActiveAccountId is null) return null;
            var result = await _backend.AuthenticateAsync(_snapshot.Copy().Accounts, ActiveAccountId, false, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await CommitAsync(new AccountVaultSnapshot(result.Accounts, result.Session.UUID), cancellationToken);
            return result.Session;
        }
        finally { _operation.Release(); }
    }

    public async Task<MSession> GetSessionForAccountAsync(string id, CancellationToken cancellationToken)
    {
        await _operation.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken); RequireAccount(id);
            var result = await _backend.AuthenticateAsync(_snapshot.Copy().Accounts, id, false, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await CommitAsync(new AccountVaultSnapshot(result.Accounts, _snapshot.ActiveAccountId), cancellationToken);
            return result.Session;
        }
        finally { _operation.Release(); }
    }

    public async Task SelectAccountAsync(string id, CancellationToken cancellationToken)
    {
        await _operation.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            RequireAccount(id);
            if (id != ActiveAccountId)
                await CommitAsync(_snapshot.Copy() with { ActiveAccountId = id }, cancellationToken);
        }
        finally { _operation.Release(); }
    }

    public async Task RemoveAccountAsync(string id, CancellationToken cancellationToken)
    {
        await _operation.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await RemoveCoreAsync(id, cancellationToken);
        }
        finally { _operation.Release(); }
    }

    public async Task SignOutAsync()
    {
        await _operation.WaitAsync();
        try
        {
            await EnsureInitializedAsync(CancellationToken.None);
            if (ActiveAccountId is not null)
                await RemoveCoreAsync(ActiveAccountId, CancellationToken.None);
        }
        finally { _operation.Release(); }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;
        var loaded = await _vault.LoadAsync(cancellationToken);
        var accounts = _backend.GetAccounts(loaded.Accounts);
        if (loaded.ActiveAccountId is not null && !accounts.Any(account => account.Id == loaded.ActiveAccountId))
            throw new AccountStorageException("Активный аккаунт не найден в защищённом хранилище. Исходный файл сохранён.");
        _snapshot = loaded.Copy();
        Accounts = accounts;
        _initialized = true;
    }

    private async Task RemoveCoreAsync(string id, CancellationToken cancellationToken)
    {
        RequireAccount(id);
        var candidate = _snapshot.Copy();
        candidate.Accounts.Remove(id);
        var selected = candidate.ActiveAccountId == id
            ? Accounts.FirstOrDefault(account => account.Id != id)?.Id
            : candidate.ActiveAccountId;
        await CommitAsync(candidate with { ActiveAccountId = selected }, cancellationToken);
    }

    private async Task CommitAsync(AccountVaultSnapshot candidate, CancellationToken cancellationToken)
    {
        var accounts = _backend.GetAccounts(candidate.Accounts);
        if (candidate.ActiveAccountId is not null && !accounts.Any(account => account.Id == candidate.ActiveAccountId))
            throw new InvalidOperationException("Сервис не вернул выбранный профиль Minecraft.");
        await _vault.SaveAsync(candidate, cancellationToken);
        _snapshot = candidate.Copy();
        Accounts = accounts;
    }

    private void RequireAccount(string id)
    {
        if (!Accounts.Any(account => account.Id == id))
            throw new InvalidOperationException("Выбранный аккаунт больше не сохранён. Обнови список аккаунтов.");
    }
}
