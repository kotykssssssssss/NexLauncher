using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CmlLib.Core.Auth;
using NexLauncher.Models;

namespace NexLauncher.Services;

/// <summary>The existing Microsoft provider contract, separate from local profile management.</summary>
public interface IMicrosoftAccountService
{
    IReadOnlyList<LauncherAccount> Accounts { get; }
    string? ActiveAccountId { get; }
    string? PlayerName { get; }
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<MSession> SignInAsync(CancellationToken cancellationToken);
    Task<MSession?> RestoreAsync(CancellationToken cancellationToken);
    /// <summary>Refresh a saved Microsoft account without changing the active launch account.</summary>
    async Task<MSession> GetSessionForAccountAsync(string id, CancellationToken token)
    {
        if (id != ActiveAccountId) throw new System.InvalidOperationException("Выбери этот Microsoft-аккаунт или используй provider с поддержкой нескольких аккаунтов.");
        return await RestoreAsync(token) ?? throw new System.InvalidOperationException("Сессия Microsoft недоступна.");
    }
    Task SelectAccountAsync(string id, CancellationToken cancellationToken);
    Task RemoveAccountAsync(string id, CancellationToken cancellationToken);
    /// <summary>Removes the active account and its local credentials; other accounts remain saved.</summary>
    Task SignOutAsync();
}

/// <summary>One account list and selection for Microsoft and local Minecraft profiles.</summary>
public interface IAccountService : IMicrosoftAccountService
{
    string? MicrosoftAvailabilityWarning { get; }
    Task<LauncherAccount> CreateLocalAccountAsync(string username, CancellationToken cancellationToken);
}
