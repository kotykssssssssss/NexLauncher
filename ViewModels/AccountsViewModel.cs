using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CmlLib.Core.Auth;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexLauncher.Models;
using NexLauncher.Services;

namespace NexLauncher.ViewModels;

/// <summary>Public account presentation only. Credentials never enter bindings.</summary>
public partial class AccountsViewModel : ObservableObject, IDisposable
{
    private readonly IAccountService _service;
    private readonly Func<Func<CancellationToken, Task>, Task> _run;
    private readonly Action _changed;
    private CancellationTokenSource? _avatarRequest;
    private bool _disposed;

    public ObservableCollection<LauncherAccount> Items { get; } = new();
    [ObservableProperty] private LauncherAccount? _selectedAccount;
    [ObservableProperty] private string _status = "Войди через Microsoft или создай локальный аккаунт.";
    [ObservableProperty] private string _localUsername = "";
    [ObservableProperty] private string _localError = "";
    [ObservableProperty] private CroppedBitmap? _avatar;
    private LauncherAccount? ActiveAccount => Items.FirstOrDefault(x => x.Id == _service.ActiveAccountId);
    public bool HasAccount => ActiveAccount is not null;
    public bool HasNoAccount => !HasAccount;
    public bool IsLocalAccount => ActiveAccount?.Type == AccountType.Local;
    public string AccountTypeLabel => ActiveAccount?.TypeLabel ?? "";
    public string Username => ActiveAccount?.Username ?? "Без аккаунта";
    public string Initial => HasAccount ? Username[..1].ToUpperInvariant() : "N";
    public bool HasAvatar => Avatar is not null;
    public bool HasNoAvatar => !HasAvatar;
    public bool HasLocalError => LocalError.Length > 0;
    public string MicrosoftWarning => _service.MicrosoftAvailabilityWarning ?? "";
    public bool HasMicrosoftWarning => MicrosoftWarning.Length > 0;
    public const string LocalLimitations = "Локальный аккаунт не авторизован в Microsoft. Он предназначен для одиночной игры и серверов, допускающих offline-клиентов. Серверы online-mode требуют Microsoft-вход.";
    public IAsyncRelayCommand AddCommand { get; }
    public IAsyncRelayCommand CreateLocalCommand { get; }
    public IAsyncRelayCommand ActivateCommand { get; }
    public IAsyncRelayCommand RemoveCommand { get; }
    public SkinManagerViewModel? Skin { get; }

    public AccountsViewModel(IAccountService service, Func<Func<CancellationToken, Task>, Task> run,
        Func<bool> canEdit, Action changed, SkinManagerViewModel? skin = null)
    {
        Skin = skin;
        _service = service; _run = run; _changed = changed;
        AddCommand = new AsyncRelayCommand(() => RunAsync(async token =>
        {
            Status = "Вход Microsoft… Закрой окно входа или нажми «Отменить», чтобы отменить.";
            await _service.SignInAsync(token);
            Refresh();
            Status = "Microsoft · " + Username + ". Сессия обновляется перед запуском игры.";
        }), canEdit);
        CreateLocalCommand = new AsyncRelayCommand(CreateLocalAsync, canEdit);
        ActivateCommand = new AsyncRelayCommand(() => RunAsync(async token =>
        {
            if (SelectedAccount is not { } selected) return;
            await _service.SelectAccountAsync(selected.Id, token);
            Refresh();
            Status = "Активный аккаунт: " + Username + " · " + AccountTypeLabel;
        }), () => canEdit() && SelectedAccount is not null && SelectedAccount.Id != _service.ActiveAccountId);
        RemoveCommand = new AsyncRelayCommand(() => RunAsync(async token =>
        {
            if (SelectedAccount is not { } selected) return;
            await _service.RemoveAccountAsync(selected.Id, token);
            Refresh();
            Status = "Аккаунт удалён с этого устройства.";
        }), () => canEdit() && SelectedAccount is not null);
    }

    private async Task CreateLocalAsync()
    {
        if (!LocalAccountIdentity.IsValidUsername(LocalUsername))
        {
            LocalError = "Ник должен содержать от 3 до 16 символов: латинские буквы, цифры или _. Пробелы не допускаются.";
            return;
        }
        LocalError = "";
        await RunAsync(async token =>
        {
            await _service.CreateLocalAccountAsync(LocalUsername, token);
            Refresh();
            Status = "Локальный · " + Username + ". Вход Microsoft для этого профиля не выполняется.";
        });
    }

    private Task RunAsync(Func<CancellationToken, Task> action) => _run(async token =>
    {
        try { await action(token); }
        catch (OperationCanceledException) { Refresh(); Status = "Операция с аккаунтом отменена."; throw; }
        catch (Exception ex) { Refresh(); Status = ex.Message; throw; }
        finally
        {
            OnPropertyChanged(nameof(MicrosoftWarning));
            OnPropertyChanged(nameof(HasMicrosoftWarning));
        }
    });

    public async Task InitializeAsync(CancellationToken token = default)
    {
        await _service.InitializeAsync(token);
        Refresh();
        if (HasAccount)
            Status = IsLocalAccount ? "Локальный · " + Username + ". Аккаунт сохранён на этом устройстве."
                : "Microsoft · " + Username + ". Вход проверится при запуске.";
    }

    public async Task<MSession?> GetSessionAsync(CancellationToken token)
    {
        Status = IsLocalAccount ? "Подготовка локальной сессии…" : "Обновляем сессию Microsoft / Minecraft…";
        try
        {
            // Choosing a local account never invokes interactive Microsoft login.
            var session = await _service.RestoreAsync(token);
            Refresh();
            Status = session is null ? "Выбери аккаунт перед запуском." : AccountTypeLabel + " · " + Username;
            return session;
        }
        catch (Exception ex) { Refresh(); Status = ex.Message; throw; }
    }

    public void SetError(string message) => Status = message;
    partial void OnSelectedAccountChanged(LauncherAccount? value) { Skin?.SetAccount(value); RefreshCommands(); }
    partial void OnLocalUsernameChanged(string value) => LocalError = "";
    partial void OnLocalErrorChanged(string value) => OnPropertyChanged(nameof(HasLocalError));
    partial void OnAvatarChanged(CroppedBitmap? value)
    {
        OnPropertyChanged(nameof(HasAvatar)); OnPropertyChanged(nameof(HasNoAvatar));
    }
    public void RefreshCommands()
    {
        Skin?.RefreshCommands();
        AddCommand?.NotifyCanExecuteChanged();
        CreateLocalCommand?.NotifyCanExecuteChanged();
        ActivateCommand?.NotifyCanExecuteChanged();
        RemoveCommand?.NotifyCanExecuteChanged();
    }

    private void Refresh()
    {
        Items.Clear();
        foreach (var account in _service.Accounts) Items.Add(account);
        SelectedAccount = ActiveAccount;
        foreach (var name in new[] { nameof(Username), nameof(Initial), nameof(HasAccount), nameof(HasNoAccount),
            nameof(IsLocalAccount), nameof(AccountTypeLabel), nameof(MicrosoftWarning), nameof(HasMicrosoftWarning) })
            OnPropertyChanged(name);
        RefreshCommands();
        _changed();
        _avatarRequest?.Cancel();
        _avatarRequest?.Dispose();
        _avatarRequest = new CancellationTokenSource();
        _ = LoadAvatarAsync(IsLocalAccount ? null : ActiveAccount?.SkinUrl, _avatarRequest.Token);
    }

    private async Task LoadAvatarAsync(string? url, CancellationToken token)
    {
        var old = Avatar;
        Avatar = null;
        (old?.Source as IDisposable)?.Dispose();
        var image = await MinecraftAvatar.LoadAsync(url, token);
        if (_disposed || token.IsCancellationRequested) { (image?.Source as IDisposable)?.Dispose(); return; }
        Avatar = image;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Skin?.Dispose();
        _avatarRequest?.Cancel(); _avatarRequest?.Dispose();
        (Avatar?.Source as IDisposable)?.Dispose();
        Avatar = null;
    }
}


