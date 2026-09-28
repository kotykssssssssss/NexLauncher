using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexLauncher.Models;

namespace NexLauncher.ViewModels;

public sealed partial class ModrinthViewModel
{
    private sealed class BrowserState
    {
        public string Query = "";
        public int Offset;
        public double Scroll;
        public string? Instance;
        public bool Installed;
        public ModrinthFiltersViewModel Filters { get; } = new();
    }
    private readonly BrowserState _packBrowser = new(), _modBrowser = new();
    private bool _opened, _restoringBrowser, _filtersLoaded;
    private CancellationTokenSource? _filterRequest;
    private Func<GameInstance, string>? _instancePath;
    [ObservableProperty] private GameInstance? _targetInstance;
    [ObservableProperty] private bool _showInstalled;
    [ObservableProperty] private string _filtersMessage = "";
    public ObservableCollection<GameInstance> TargetInstances { get; } = new();
    public ModrinthFiltersViewModel Filters => (IsPacks ? _packBrowser : _modBrowser).Filters;
    public double ScrollOffset { get; set; }
    public int SearchRevision => _offset;
    public bool CanSearch => IsPacks || _instance is { Loader: not ModLoader.Vanilla };
    public bool NeedsInstance => IsMods && !CanSearch;
    public bool ShowSearchResults => !ShowInstalled || IsPacks;
    public string PageLabel => _total == 0 ? "0 проектов" : $"{_offset + 1}–{_offset + Results.Count} из {_total:N0}";
    public string InstalledLabel => $"Установленные ({Installed.Count})";
    public bool IsSelectedInstalled => !IsPacks && SelectedVersion is { } version && Installed.Any(x => x.Version.Id == version.Id);
    public bool IsProjectInstalled => !IsPacks && Details is { } detail && Installed.Any(x => x.Version.ProjectId == detail.Project.Id);
    public string InstallLabel => IsPacks ? "Установить сборку" : IsSelectedInstalled ? "Установлено" :
        _plannedVersion == SelectedVersion?.Id && SelectedVersion is not null ? "Подтвердить установку" : IsProjectInstalled ? "Обновить / сменить версию…" : "Установить мод…";
    public string VersionSummary => SelectedVersion?.SelectionSummary ?? "Выбери совместимую версию";
    public IRelayCommand PacksModeCommand { get; private set; } = null!;
    public IRelayCommand ModsModeCommand { get; private set; } = null!;
    public IRelayCommand BrowseModsCommand { get; private set; } = null!;
    public IRelayCommand InstalledModsCommand { get; private set; } = null!;
    public IRelayCommand ResetFiltersCommand { get; private set; } = null!;
    public IAsyncRelayCommand RetryFiltersCommand { get; private set; } = null!;
    public IAsyncRelayCommand PrimaryActionCommand { get; private set; } = null!;
    public IAsyncRelayCommand RemoveProjectCommand { get; private set; } = null!;
    public IRelayCommand<ModrinthResultViewModel> OpenProjectCommand { get; private set; } = null!;
    public IRelayCommand<InstalledMod> OpenInstalledProjectCommand { get; private set; } = null!;

    private void InitializeBrowserCommands()
    {
        _packBrowser.Filters.PropertyChanged += FiltersChanged;
        _modBrowser.Filters.PropertyChanged += FiltersChanged;
        PacksModeCommand = new RelayCommand(() => Open(true, TargetInstance, _game), () => IsEditable);
        ModsModeCommand = new RelayCommand(() => Open(false, TargetInstance, TargetInstance is { } i ? _instancePath?.Invoke(i) ?? _game : ""), () => IsEditable);
        BrowseModsCommand = new RelayCommand(() => ShowInstalled = false);
        InstalledModsCommand = new RelayCommand(() => ShowInstalled = true);
        ResetFiltersCommand = new RelayCommand(() =>
        { _restoringBrowser = true; Filters.Reset(); _restoringBrowser = false; _offset = 0; if (CanSearch) _ = SearchAsync(false); }, () => IsEditable);
        RetryFiltersCommand = new AsyncRelayCommand(LoadFiltersAsync, () => _filterRequest is null);
        PrimaryActionCommand = new AsyncRelayCommand(() => IsPacks || _plannedVersion == SelectedVersion?.Id ? InstallAsync() : PlanAsync(),
            () => IsEditable && HasDetails && !IsLoadingDetails && SelectedVersion is not null && !IsSelectedInstalled);
        RemoveProjectCommand = new AsyncRelayCommand(async () =>
        { SelectedInstalled = Installed.FirstOrDefault(x => x.Version.ProjectId == Details?.Project.Id); await RemoveAsync(); }, () => IsEditable && IsProjectInstalled);
        OpenProjectCommand = new RelayCommand<ModrinthResultViewModel>(x => { if (x is not null) SelectedResult = x; }, _ => IsEditable);
        OpenInstalledProjectCommand = new RelayCommand<InstalledMod>(x => { if (x is not null) OpenInstalledDetails(x); }, _ => IsEditable);
    }
    public void ConfigureInstances(IEnumerable<GameInstance> instances, GameInstance? preferred, Func<GameInstance, string> path)
    {
        _instancePath = path; _restoringBrowser = true;
        var previous = TargetInstance?.Id;
        TargetInstances.Clear(); foreach (var item in instances.Where(x => x.Loader != ModLoader.Vanilla)) TargetInstances.Add(item);
        TargetInstance = TargetInstances.FirstOrDefault(x => x.Id == previous) ?? TargetInstances.FirstOrDefault(x => x.Id == preferred?.Id) ?? TargetInstances.FirstOrDefault();
        _restoringBrowser = false;
    }
    public void ResumeBrowser()
    {
        var packs = !_opened || IsPacks;
        Open(packs, TargetInstance, TargetInstance is { } i ? _instancePath?.Invoke(i) ?? _game : "");
    }
    partial void OnTargetInstanceChanged(GameInstance? value)
    {
        if (!_restoringBrowser && IsMods && _active)
            Open(false, value, value is null ? "" : _instancePath?.Invoke(value) ?? _game);
    }
    partial void OnShowInstalledChanged(bool value) => OnPropertyChanged(nameof(ShowSearchResults));
    private void FiltersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_restoringBrowser || !ReferenceEquals(sender, Filters) || !_active || IsDetails || !CanSearch) return;
        _offset = 0; _ = SearchAsync(true);
    }
    private void SaveBrowserState()
    {
        if (!_opened) return;
        var state = IsPacks ? _packBrowser : _modBrowser;
        state.Query = Query; state.Offset = _offset; state.Scroll = ScrollOffset; state.Instance = _instance?.Id; state.Installed = ShowInstalled;
    }
    private void RestoreBrowserState(bool packs, GameInstance? instance)
    {
        _restoringBrowser = true;
        var state = packs ? _packBrowser : _modBrowser;
        Query = state.Query; _offset = packs || state.Instance == instance?.Id ? state.Offset : 0;
        ScrollOffset = _offset == state.Offset ? state.Scroll : 0; ShowInstalled = !packs && state.Installed;
        if (!packs) TargetInstance = instance;
        _opened = true; _restoringBrowser = false;
        OnPropertyChanged(nameof(Filters)); OnPropertyChanged(nameof(CanSearch)); OnPropertyChanged(nameof(NeedsInstance)); OnPropertyChanged(nameof(ShowSearchResults));
        RefreshCommands();
    }
    private async Task LoadFiltersAsync()
    {
        if (_filterRequest is not null || _disposed) return;
        using var request = new CancellationTokenSource(TimeSpan.FromSeconds(45)); _filterRequest = request;
        FiltersMessage = "Загрузка фильтров…"; RetryFiltersCommand?.NotifyCanExecuteChanged();
        try
        {
            var catalog = await _api.FilterCatalogAsync(request.Token);
            if (_disposed) return;
            _restoringBrowser = true;
            try { _packBrowser.Filters.Populate(catalog, true); _modBrowser.Filters.Populate(catalog, false); }
            finally { _restoringBrowser = false; }
            _filtersLoaded = true; FiltersMessage = "";
        }
        catch (OperationCanceledException) { if (!_disposed) FiltersMessage = "Фильтры не загружены. Повтори запрос."; }
        catch (Exception ex) { if (!_disposed) { FiltersMessage = "Справочник фильтров недоступен. Поиск остаётся доступен."; _log(ex.ToString()); } }
        finally { _filterRequest = null; RetryFiltersCommand?.NotifyCanExecuteChanged(); }
    }
    private void RefreshBrowserCommands()
    {
        foreach (var name in new[] { nameof(PageLabel), nameof(InstalledLabel), nameof(InstallLabel), nameof(IsSelectedInstalled), nameof(IsProjectInstalled), nameof(VersionSummary) }) OnPropertyChanged(name);
        PrimaryActionCommand?.NotifyCanExecuteChanged(); RemoveProjectCommand?.NotifyCanExecuteChanged();
        PacksModeCommand?.NotifyCanExecuteChanged(); ModsModeCommand?.NotifyCanExecuteChanged(); ResetFiltersCommand?.NotifyCanExecuteChanged();
        OpenProjectCommand?.NotifyCanExecuteChanged(); OpenInstalledProjectCommand?.NotifyCanExecuteChanged();
    }
}
