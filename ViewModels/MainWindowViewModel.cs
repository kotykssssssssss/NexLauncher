using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CmlLib.Core.Auth;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexLauncher.Models;
using NexLauncher.Services;
using NexLauncher.Services.Skins;
using NexLauncher.Services.Skins.Catalog;
using NexLauncher.Services.Loaders;
using NexLauncher.Services.Network;
using NexLauncher.Services.Storage;
using NexLauncher.Services.Modrinth;

namespace NexLauncher.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly ConfigurationStore _store;
    private readonly IMinecraftService _minecraft;

    private readonly LauncherLog _log;
    private IReadOnlyList<MinecraftRelease> _allVersions = Array.Empty<MinecraftRelease>();
    private LauncherConfiguration _configuration = new();
    private CancellationTokenSource? _operation;
    private bool _initialized;
    private bool _loadingSelection;
    private bool _loadingConfiguration;
    private bool _configurationAvailable = true;

    public ObservableCollection<GameInstance> Instances { get; } = new();
    public ObservableCollection<MinecraftRelease> Versions { get; } = new();

    [ObservableProperty] private GameInstance? _selectedInstance;
    [ObservableProperty] private MinecraftRelease? _selectedVersion;
    [ObservableProperty] private string _newInstanceName = "Новая сборка";
    [ObservableProperty] private string _currentPage = "play";
    [ObservableProperty] private string _statusText = "Выбери версию и создай свою первую сборку.";
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private string _notice = "";

    [ObservableProperty] private string _javaPath = "";
    [ObservableProperty] private string _instancesDirectory = "";
    public Func<Task<string?>>? PickInstancesFolderAsync { get; set; }
    public IAsyncRelayCommand ChooseInstancesFolderCommand { get; }
    public LoaderOptionsViewModel LoaderOptions { get; }
    [ObservableProperty] private int _memoryGb = 4;
    [ObservableProperty] private bool _includeSnapshots;
    [ObservableProperty] private bool _isWorking;
    [ObservableProperty] private bool _isLoadingVersions;
    [ObservableProperty] private bool _isGameRunning;
    [ObservableProperty] private bool _isProgressIndeterminate;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _showLog;
    [ObservableProperty] private string _logText = "";

    public bool IsPlayPage => CurrentPage == "play";
    public bool IsInstancesPage => CurrentPage == "instances";
    public bool IsSettingsPage => CurrentPage == "settings";
    public bool IsModrinthPage => CurrentPage is "modrinth" or "mods";
    public bool IsSkinsPage => CurrentPage == "skins";
    public bool IsStandardPage => !IsModrinthPage && !IsSkinsPage;
    public SkinCatalogViewModel SkinCatalog { get; }
    public bool IsPacksPage => IsModrinthPage;
    public bool CanOpenMods => SelectedInstance is { Loader: not ModLoader.Vanilla };
    public ModrinthViewModel Catalog { get; }
    public bool HasInstance => SelectedInstance is not null;
    public bool HasNoInstances => Instances.Count == 0;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasNotice => !string.IsNullOrWhiteSpace(Notice);
    public bool HasAccount => Accounts.HasAccount;
    public bool HasNoAccount => !HasAccount;
    public bool IsEditable => !IsWorking && !IsLoadingVersions && !IsGameRunning && _configurationAvailable && QuickCss?.IsBusy != true;
    public bool CanCancel => IsWorking && !IsGameRunning && _operation is not null;
    public bool ShowProgress => IsWorking && !IsGameRunning && _operation is not null;
    public bool IsInstalled => SelectedInstance is not null && _minecraft.IsInstalled(SelectedInstance);
    public string AccountName => Accounts.Username;
    public string InstanceTitle => SelectedInstance?.Name ?? "Твоя первая сборка";
    public string InstanceDetails => SelectedInstance?.Details ?? "Minecraft: Java Edition";
    public string InstallState => IsGameRunning ? "Игра запущена" : IsInstalled ? "Установлено" : "Нужна установка";
    public string MemoryLabel => $"{MemoryGb} ГБ";
    public string GameDirectory => SelectedInstance is null ? _store.DataDirectory : SafePaths.GamePath(_store.DataDirectory, SelectedInstance);
    public string DataDirectory => _store.DataDirectory;
    public string VersionCount => IsLoadingVersions ? "Обновляем список…" : $"{Versions.Count} версий";
    public string PrimaryButtonText => IsWorking ? (IsGameRunning ? "Игра запущена" : "Подготовка…") :
        !HasInstance ? "Создать сборку" : !IsInstalled ? "Установить" : !HasAccount ? "Выбрать аккаунт" : Accounts.IsLocalAccount ? "Играть локально" : "Играть";
    public string HeroTitle => IsGameRunning ? "Хорошей игры" : !HasInstance ? "Начни свой новый мир" :
        IsInstalled ? "Всё готово к игре" : "Подготовим игру";

    public IRelayCommand<string> NavigateCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand DismissErrorCommand { get; }
    public IRelayCommand ToggleLogCommand { get; }
    public IRelayCommand OpenFolderCommand { get; }
    public IRelayCommand OpenLogCommand { get; }
    public AccountsViewModel Accounts { get; }
    public QuickCssViewModel QuickCss { get; }
    public IAsyncRelayCommand RefreshVersionsCommand { get; }
    public IAsyncRelayCommand CreateInstanceCommand { get; }
    public IAsyncRelayCommand PrimaryCommand { get; }
    public IAsyncRelayCommand SignInCommand { get; }
    public IAsyncRelayCommand SaveSettingsCommand { get; }

    public MainWindowViewModel(ConfigurationStore store, IMinecraftService minecraft, IAccountService accounts, ILoaderCatalog? loaderCatalog = null, IModrinthService? modrinth = null, ISkinService? skins = null)
    {
        _store = store;
        _minecraft = minecraft;
        LoaderOptions = new LoaderOptionsViewModel(loaderCatalog ?? new LoaderCatalog(LauncherHttp.Shared), RefreshState);
        InstancesDirectory = Path.Combine(store.DataDirectory, "instances");
        var skinValidator = new SkinValidator();
        var skinService = skins ?? new SkinService(new MinecraftSkinService(accounts, MinecraftSkinApi.Shared, LauncherHttp.Shared, skinValidator),
            new OfflineSkinService(new SkinStorage(store.DataDirectory, skinValidator)));
        Accounts = new AccountsViewModel(accounts, RunAccountOperationAsync, () => IsEditable, RefreshState,
            new SkinManagerViewModel(skinService, skinValidator, RunAccountOperationAsync, () => IsEditable));
        QuickCss = new QuickCssViewModel(store.DataDirectory, SaveQuickCssAsync, () => IsEditable);
        QuickCss.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(QuickCssViewModel.IsBusy)) RefreshState();
        };
        _log = new LauncherLog(store.DataDirectory);
        var skinLibrary = new SkinLibrary(store.DataDirectory, skinValidator);
        SkinCatalog = new SkinCatalogViewModel(skinLibrary, skinLibrary, new SkinCatalogPreviewCache(),
            new SkinPngExport(skinValidator), () => IsEditable, PrepareLibrarySkinAsync, AppendLog);
        Catalog = new ModrinthViewModel(modrinth ?? new ModrinthService(LauncherHttp.Shared), LauncherHttp.Shared, minecraft,
            RunOperationAsync, CreateProgress, () => IsEditable, () => InstancesDirectory, PublishPackAsync, AppendLog);
        Instances.CollectionChanged += (_, _) =>
        {
            if (IsModrinthPage) Catalog.ConfigureInstances(Instances, SelectedInstance, instance => instance.GameDirectory);
        };
        NavigateCommand = new RelayCommand<string>(page =>
        {
            if (page is "play" or "instances" or "settings" or "modrinth" or "mods" or "skins") CurrentPage = page;
        });
        CancelCommand = new RelayCommand(() => _operation?.Cancel(), () => CanCancel);
        DismissErrorCommand = new RelayCommand(() => ErrorMessage = "");
        ToggleLogCommand = new RelayCommand(() => ShowLog = !ShowLog);
        OpenFolderCommand = new RelayCommand(() => OpenPath(GameDirectory, true), () => HasInstance);
        OpenLogCommand = new RelayCommand(() => { _log.Write("Открытие журнала."); OpenPath(_log.FilePath, false); });
        RefreshVersionsCommand = new AsyncRelayCommand(RefreshVersionsAsync, () => IsEditable);
        CreateInstanceCommand = new AsyncRelayCommand(CreateInstanceAsync, () => IsEditable && LoaderOptions.CanCreate && SelectedVersion is not null && !string.IsNullOrWhiteSpace(NewInstanceName));
        ChooseInstancesFolderCommand = new AsyncRelayCommand(ChooseInstancesFolderAsync, () => IsEditable);
        PrimaryCommand = new AsyncRelayCommand(InstallOrPlayAsync, () => IsEditable);
        SignInCommand = Accounts.AddCommand;
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync, () => IsEditable);
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            _loadingConfiguration = true;
            _configuration = await _store.LoadAsync();
            InstancesDirectory = string.IsNullOrEmpty(_configuration.InstancesDirectory) ? Path.Combine(_store.DataDirectory, "instances") : _configuration.InstancesDirectory;
            await QuickCss.InitializeAsync(_configuration.QuickCss);
            IncludeSnapshots = _configuration.ShowSnapshots;
            foreach (var instance in _configuration.Instances) Instances.Add(instance);
            SelectedInstance = Instances.FirstOrDefault(x => x.Id == _configuration.SelectedInstanceId) ?? Instances.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _configurationAvailable = false;
            ShowError(ex, "Не удалось прочитать настройки. Исходный файл сохранён; исправь его и перезапусти лаунчер.");
        }
        RefreshState();
        _loadingConfiguration = false;
        try { await Accounts.InitializeAsync(); }
        catch (Exception ex)
        {
            Accounts.SetError(ex.Message);
            // Authentication errors are sanitized by the service; never log credential objects.
            ErrorMessage = "Не удалось загрузить аккаунты. " + ex.Message;
        }
        if (_configurationAvailable) await RefreshVersionsAsync();
    }

    partial void OnCurrentPageChanged(string value)
    {
        if (SkinCatalog is not null)
        {
            if (value == "skins") SkinCatalog.Open(Accounts.Items, Accounts.SelectedAccount);
            else SkinCatalog.Deactivate();
        }
        if (Catalog is not null)
        {
            if (value is "modrinth" or "mods")
            {
                Catalog.ConfigureInstances(Instances, SelectedInstance, instance => instance.GameDirectory);
                if (value == "mods") Catalog.Open(false, SelectedInstance, GameDirectory);
                else Catalog.ResumeBrowser();
            }
            else Catalog.Deactivate();
        }
        RefreshState();
    }
    partial void OnSelectedInstanceChanged(GameInstance? value)
    {
        _loadingSelection = true;
        MemoryGb = (value?.MemoryMb ?? 4096) / 1024;
        JavaPath = value?.JavaPath ?? "";
        _loadingSelection = false;
        if (CurrentPage == "mods" && Catalog?.IsMods == true) Catalog.Open(false, value, GameDirectory);
        StatusText = value is null ? "Выбери версию и создай свою первую сборку." :
            _minecraft.IsInstalled(value) ? "Сборка готова. Можно запускать." : "Файлы игры ещё не установлены.";
        if (value is not null)
        {
            _configuration.SelectedInstanceId = value.Id;
            if (_initialized && !_loadingConfiguration && !IsWorking) _ = SaveSelectionAsync();
        }
        RefreshState();
    }
    partial void OnSelectedVersionChanged(MinecraftRelease? value) { LoaderOptions.SetMinecraft(value?.Id); RefreshState(); }
    partial void OnNewInstanceNameChanged(string value) => RefreshState();
    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnNoticeChanged(string value) => OnPropertyChanged(nameof(HasNotice));
    partial void OnMemoryGbChanged(int value) => OnPropertyChanged(nameof(MemoryLabel));
    partial void OnIncludeSnapshotsChanged(bool value) => FilterVersions();
    partial void OnIsWorkingChanged(bool value) => RefreshState();
    partial void OnIsLoadingVersionsChanged(bool value) => RefreshState();
    partial void OnIsGameRunningChanged(bool value) => RefreshState();

    private void RefreshState()
    {
        foreach (var name in new[] { nameof(IsPlayPage), nameof(IsInstancesPage), nameof(IsSettingsPage), nameof(IsModrinthPage), nameof(IsPacksPage), nameof(IsSkinsPage), nameof(IsStandardPage), nameof(CanOpenMods),
            nameof(HasInstance), nameof(HasNoInstances), nameof(HasAccount), nameof(HasNoAccount), nameof(IsEditable),
            nameof(CanCancel), nameof(ShowProgress), nameof(IsInstalled), nameof(AccountName), nameof(InstanceTitle),
            nameof(InstanceDetails), nameof(InstallState), nameof(GameDirectory), nameof(PrimaryButtonText),
            nameof(HeroTitle), nameof(VersionCount) })
            OnPropertyChanged(name);
        // Property setters also run during initialization, before every command has been assigned.
        CancelCommand?.NotifyCanExecuteChanged();
        OpenFolderCommand?.NotifyCanExecuteChanged();
        RefreshVersionsCommand?.NotifyCanExecuteChanged();
        CreateInstanceCommand?.NotifyCanExecuteChanged();
        PrimaryCommand?.NotifyCanExecuteChanged();
        SignInCommand?.NotifyCanExecuteChanged();
        SaveSettingsCommand?.NotifyCanExecuteChanged();
        ChooseInstancesFolderCommand?.NotifyCanExecuteChanged();
        Accounts?.RefreshCommands();
        QuickCss?.RefreshCommands();
        Catalog?.RefreshCommands();
        SkinCatalog?.RefreshCommands();
    }

    private void FilterVersions()
    {
        var selectedId = SelectedVersion?.Id;
        Versions.Clear();
        foreach (var version in _allVersions.Where(x => x.Type == "release" || (IncludeSnapshots && x.Type == "snapshot"))
                     .OrderByDescending(x => x.ReleasedAt))
            Versions.Add(version);
        SelectedVersion = Versions.FirstOrDefault(x => x.Id == selectedId) ?? Versions.FirstOrDefault();
        RefreshState();
    }

    private async Task RefreshVersionsAsync()
    {
        IsLoadingVersions = true;
        ErrorMessage = "";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            _allVersions = await _minecraft.GetVersionsAsync(timeout.Token);
            FilterVersions();
            if (Versions.Count == 0)
                ErrorMessage = "Список версий пуст. Проверь соединение и нажми «Обновить».";
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Сервер версий не ответил вовремя. Проверь соединение и повтори.";
        }
        catch (Exception ex) { ShowError(ex, "Не удалось получить версии. Уже созданные сборки остаются доступны."); }
        finally { IsLoadingVersions = false; }
    }

    private async Task SaveSelectionAsync()
    {
        try { await PersistAsync(); }
        catch (Exception ex) { ShowError(ex, "Не удалось сохранить выбранную сборку."); }
    }

    private async Task CreateInstanceAsync()
    {
        if (!IsEditable || SelectedVersion is null || !LoaderOptions.CanCreate) return;
        var name = NewInstanceName.Trim();
        if (name.Length is < 1 or > 60)
        {
            ErrorMessage = "Название сборки должно содержать от 1 до 60 символов.";
            return;
        }
        IsWorking = true;
        var instance = new GameInstance { Name = name, VersionId = SelectedVersion.Id, Loader = LoaderOptions.Loader, LoaderVersion = LoaderOptions.SelectedRelease?.Version ?? "" };
        try
        {
            var root = await SafePaths.CheckWritableRootAsync(InstancesDirectory, CancellationToken.None);
            instance.GameDirectory = SafePaths.Resolve(root, instance.Id + "/game");
            Instances.Add(instance);
            SelectedInstance = instance;
            await PersistAsync();
            CurrentPage = "play";
            Notice = "Сборка создана. Теперь можно установить игру.";
            ErrorMessage = "";
        }
        catch (Exception ex)
        {
            Instances.Remove(instance);
            SelectedInstance = Instances.FirstOrDefault();
            ShowError(ex, "Не удалось сохранить сборку.");
        }
        finally { IsWorking = false; }
        RefreshState();
    }

    private async Task InstallOrPlayAsync()
    {
        if (!IsEditable) return;
        if (SelectedInstance is null) { CurrentPage = "instances"; return; }
        var instance = SelectedInstance;
        await RunOperationAsync(async token =>
        {
            await PersistAsync(token);
            if (!_minecraft.IsInstalled(instance))
            {
                await _minecraft.InstallAsync(instance, CreateProgress(), token);
                StatusText = "Установка завершена. Сборка готова к запуску.";
                Notice = HasAccount ? "Можно нажимать «Играть»." : "Выбери Microsoft или локальный аккаунт в настройках.";
                return;
            }
            if (!HasAccount)
            {
                CurrentPage = "settings";
                Notice = "Войди через Microsoft или создай локальный аккаунт.";
                return;
            }
            var session = await GetAccountSessionAsync(token);
            if (session is null) return;
            StatusText = "Проверяем файлы и готовим Java…";
            var exitCode = await _minecraft.LaunchAsync(instance, session, CreateProgress(), AppendLog, token);
            StatusText = exitCode == 0 ? "Игра закрыта. Можно запускать снова." : $"Игра завершилась с кодом {exitCode}.";
            if (exitCode != 0) ErrorMessage = "Minecraft завершился с ошибкой. Открой журнал, чтобы посмотреть причину.";
        });
    }

    private async Task<MSession?> GetAccountSessionAsync(CancellationToken token)
    {
        StatusText = "Проверяем аккаунт Minecraft…";
        var session = await Accounts.GetSessionAsync(token);
        RefreshState();
        return session;
    }

    private IProgress<LaunchProgress> CreateProgress()
    {
        var operation = _operation;
        return new UiProgress(value =>
        {
            if (!IsWorking || !ReferenceEquals(operation, _operation)) return;
            StatusText = value.Message;
            IsProgressIndeterminate = value.Percent is null;
            ProgressValue = Math.Clamp(value.Percent ?? 0, 0, 100);
            if (value.IsRunning) IsGameRunning = true;
        });
    }

    private sealed class UiProgress(Action<LaunchProgress> report) : IProgress<LaunchProgress>
    {
        public void Report(LaunchProgress value)
        {
            if (Dispatcher.UIThread.CheckAccess()) report(value);
            else Dispatcher.UIThread.Post(() => report(value));
        }
    }
    private Task RunAccountOperationAsync(Func<CancellationToken, Task> action)
    {
        StatusText = "Аккаунты Minecraft…";
        return RunOperationAsync(async token =>
        {
            await action(token);
            StatusText = Accounts.HasAccount
                ? Accounts.Username + " · " + Accounts.AccountTypeLabel
                : "Выбери Microsoft или локальный аккаунт.";
        });
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> action)
    {
        if (IsWorking) return;
        using var operation = new CancellationTokenSource();
        _operation = operation;
        IsWorking = true;
        IsProgressIndeterminate = true;
        ProgressValue = 0;
        ErrorMessage = "";
        Notice = "";
        try { await action(operation.Token); }
        catch (OperationCanceledException)
        {
            StatusText = "Операция отменена.";
            Notice = "Можно повторить действие.";
            Accounts.SetError("Операция отменена.");
        }
        catch (Exception ex) { ShowError(ex, "Не удалось завершить действие."); StatusText = "Нужна помощь — смотри сообщение ниже."; }
        finally { _operation = null; IsGameRunning = false; IsWorking = false; RefreshState(); }
    }

    private async Task SaveSettingsAsync()
    {
        if (!IsEditable || _loadingSelection) return;
        ErrorMessage = "";
        if (MemoryGb is < 1 or > 32)
        {
            ErrorMessage = "Укажи объём памяти от 1 до 32 ГБ.";
            return;
        }
        var java = JavaPath.Trim();
        if (java.Length > 0 && (!Path.IsPathFullyQualified(java) || !File.Exists(java)))
        {
            ErrorMessage = "Укажи полный путь к существующему java или javaw, либо оставь поле пустым.";
            return;
        }
        var instance = SelectedInstance;
        var oldMemory = instance?.MemoryMb ?? 4096;
        var oldJava = instance?.JavaPath ?? "";

        IsWorking = true;
        try
        {
            if (instance is not null)
            {
                instance.MemoryMb = MemoryGb * 1024;
                instance.JavaPath = java;
            }

            await PersistAsync();
            Notice = "Настройки сохранены.";
        }
        catch (Exception ex)
        {
            if (instance is not null)
            {
                instance.MemoryMb = oldMemory;
                instance.JavaPath = oldJava;
            }

            ShowError(ex, "Не удалось сохранить настройки.");
        }
        finally { IsWorking = false; RefreshState(); }
    }
    private async Task SaveQuickCssAsync(QuickCssSettings settings)
    {
        var previous = _configuration.QuickCss;
        _configuration.QuickCss = settings;
        try { await PersistAsync(); }
        catch { _configuration.QuickCss = previous; throw; }
    }

    public void Dispose()
    {
        SkinCatalog.Dispose();
        Catalog.Dispose();
        LoaderOptions.Dispose();
        QuickCss.Dispose();
        Accounts.Dispose();
    }

    private Task PersistAsync(CancellationToken cancellationToken = default)
    {
        if (!_configurationAvailable) throw new InvalidOperationException("Настройки повреждены. Исходный файл оставлен без изменений.");
        _configuration.Instances = Instances.ToList();
        _configuration.SelectedInstanceId = SelectedInstance?.Id;

        _configuration.ShowSnapshots = IncludeSnapshots;
        _configuration.InstancesDirectory = InstancesDirectory;
        return _store.SaveAsync(_configuration, cancellationToken);
    }

    private async Task ChooseInstancesFolderAsync()
    {
        if (PickInstancesFolderAsync is null) return;
        var selected = await PickInstancesFolderAsync();
        if (selected is null) return;
        await RunOperationAsync(async token =>
        {
            var previous = InstancesDirectory;
            var root = await SafePaths.CheckWritableRootAsync(selected, token);
            InstancesDirectory = root;
            try { await PersistAsync(token); }
            catch { InstancesDirectory = previous; throw; }
            Notice = "Папка сохранена. Старые сборки остаются на прежнем месте; здесь появятся новые.";
        });
    }

    private async Task PublishPackAsync(GameInstance instance, CancellationToken token)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            await Dispatcher.UIThread.InvokeAsync(() => PublishPackAsync(instance, token));
            return;
        }
        var previous = SelectedInstance;
        Instances.Add(instance); SelectedInstance = instance;
        try { await PersistAsync(token); }
        catch { Instances.Remove(instance); SelectedInstance = previous; throw; }
        Notice = "Modpack установлен: " + instance.Name + ". Выбери «Играть» для запуска.";
        RefreshState();
    }

    private void AppendLog(string message)
    {
        var safe = LauncherLog.Sanitize(message);
        _log.Write(safe);
        void Update()
        {
            var next = LogText + safe + Environment.NewLine;
            LogText = next.Length > 18000 ? next[^18000..] : next;
        }
        if (Dispatcher.UIThread.CheckAccess()) Update();
        else Dispatcher.UIThread.Post(Update);
    }

    private void ShowError(Exception exception, string context)
    {
        ErrorMessage = context + " " + LauncherLog.Sanitize(exception.Message);
        AppendLog(exception.ToString());
    }

    private void OpenPath(string path, bool directory)
    {
        try
        {
            if (directory) Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowError(ex, "Не удалось открыть."); }
    }

    public void OnWindowClosing()
    {
        SkinCatalog.Deactivate();
        Catalog.Deactivate();
        if (!IsGameRunning) _operation?.Cancel();
    }

    private async Task PrepareLibrarySkinAsync(LauncherAccount account, SkinCatalogTexture texture)
    {
        var saved = Accounts.Items.FirstOrDefault(x => x.Id == account.Id && x.Type == account.Type);
        if (saved is null || Accounts.Skin is not { } manager) throw new SkinException("Аккаунт больше не сохранён. Выбери другой аккаунт.");
        Accounts.SelectedAccount = saved;
        if (!await manager.PrepareDraftAsync(texture.Image, texture.Model)) throw new SkinException("Не удалось подготовить черновик. Проверь аккаунт и повтори действие.");
        CurrentPage = "settings";
    }
}


