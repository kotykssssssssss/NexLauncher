using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexLauncher.Models;
using NexLauncher.Services;
using NexLauncher.Services.Modrinth;
using NexLauncher.Services.Network;

namespace NexLauncher.ViewModels;

public sealed partial class ModrinthResultViewModel(ModrinthHit hit) : ObservableObject, IDisposable
{
    public ModrinthHit Hit { get; } = hit;
    [ObservableProperty] private Bitmap? _icon;
    public void Dispose() => Icon?.Dispose();
}

public sealed partial class ModrinthViewModel : ObservableObject, IDisposable
{
    private readonly IModrinthService _api;
    private readonly LauncherHttp _http;
    private readonly ModManager _mods;
    private readonly ModpackInstaller _packs;
    private readonly Func<Func<CancellationToken, Task>, Task> _run;
    private readonly Func<IProgress<LaunchProgress>> _progress;
    private readonly Func<bool> _canEdit;
    private readonly Func<string> _root;
    private readonly Func<GameInstance, CancellationToken, Task> _publish;
    private readonly Action<string> _log;
    private GameInstance? _instance;
    private string _game = "";
    private CancellationTokenSource? _search;
    private CancellationTokenSource? _detailsRequest;
    private bool _active;
    private bool _disposed;
    private int _offset;
    private int _total;
    private int _contextGeneration;
    private int _installedGeneration;
    private string? _plannedVersion;
    public ObservableCollection<ModrinthResultViewModel> Results { get; } = new();
    public ObservableCollection<ModrinthVersion> Versions { get; } = new();
    public ObservableCollection<InstalledMod> Installed { get; } = new();
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _isPacks = true;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private bool _isDetails;
    [ObservableProperty] private bool _isLoadingDetails;
    [ObservableProperty] private ProjectDetailsViewModel? _details;
    [ObservableProperty] private string _planText = "";
    [ObservableProperty] private string _packName = "Новая сборка";
    [ObservableProperty] private bool _includeOptionalPackFiles;
    [ObservableProperty] private ModrinthResultViewModel? _selectedResult;
    [ObservableProperty] private ModrinthVersion? _selectedVersion;
    [ObservableProperty] private InstalledMod? _selectedInstalled;
    public bool IsMods => !IsPacks;
    public bool IsBrowsing => !IsDetails;
    public bool IsEditable => _canEdit();
    public bool HasDetails => Details is not null;
    public bool HasNoVersions => HasDetails && Versions.Count == 0;
    public bool HasNoInstalled => Installed.Count == 0;
    public bool HasError => Error.Length > 0;
    public bool HasPlan => PlanText.Length > 0;
    public bool HasNoResults => !IsBusy && Results.Count == 0;
    public string Context => IsPacks ? "Готовые сборки из Modrinth" : _instance is null ? "Выбери сборку с загрузчиком." : _instance.Name + " · " + _instance.Details;
    public string SearchTitle => IsPacks ? "Найти modpack" : "Найти мод для этой сборки";
    public IAsyncRelayCommand SearchCommand { get; }
    public IAsyncRelayCommand NextCommand { get; }
    public IAsyncRelayCommand PreviousCommand { get; }
    public IAsyncRelayCommand PlanCommand { get; }
    public IAsyncRelayCommand InstallCommand { get; }
    public IAsyncRelayCommand RefreshInstalledCommand { get; }
    public IAsyncRelayCommand RemoveCommand { get; }
    public IAsyncRelayCommand CheckUpdateCommand { get; }
    public IAsyncRelayCommand RetryDetailsCommand { get; }
    public IRelayCommand BackCommand { get; }
    public IRelayCommand CancelRequestCommand { get; }
    public IRelayCommand ShowInstalledCommand { get; }
    public IRelayCommand OpenModrinthCommand { get; }

    public ModrinthViewModel(IModrinthService api, LauncherHttp http, IMinecraftService minecraft,
        Func<Func<CancellationToken, Task>, Task> run, Func<IProgress<LaunchProgress>> progress, Func<bool> canEdit,
        Func<string> root, Func<GameInstance, CancellationToken, Task> publish, Action<string> log)
    {
        _api = api; _http = http; _mods = new(api, http); _packs = new(api, http, minecraft);
        _run = run; _progress = progress; _canEdit = canEdit; _root = root; _publish = publish; _log = log;
        SearchCommand = new AsyncRelayCommand(() => SearchAsync(false), () => _active && !IsBusy && _canEdit());
        NextCommand = new AsyncRelayCommand(async () => { _offset += 20; await SearchAsync(false); }, () => !IsBusy && _canEdit() && _offset + 20 < _total);
        PreviousCommand = new AsyncRelayCommand(async () => { _offset = Math.Max(0, _offset - 20); await SearchAsync(false); }, () => !IsBusy && _canEdit() && _offset > 0);
        PlanCommand = new AsyncRelayCommand(PlanAsync, () => _canEdit() && !IsLoadingDetails && SelectedVersion is not null && HasDetails && !IsPacks);
        InstallCommand = new AsyncRelayCommand(InstallAsync, () => _canEdit() && !IsLoadingDetails && SelectedVersion is not null && HasDetails && (IsPacks || _plannedVersion == SelectedVersion.Id));
        RefreshInstalledCommand = new AsyncRelayCommand(RefreshInstalledAsync, () => _canEdit() && IsMods && _instance is not null);
        RemoveCommand = new AsyncRelayCommand(RemoveAsync, () => _canEdit() && SelectedInstalled is not null);
        CheckUpdateCommand = new AsyncRelayCommand(CheckUpdateAsync, () => _canEdit() && SelectedInstalled is not null);
        ShowInstalledCommand = new RelayCommand(() => ShowInstalled(SelectedInstalled!), () => _canEdit() && SelectedInstalled is not null);
        RetryDetailsCommand = new AsyncRelayCommand(() => LoadDetailsAsync(SelectedResult), () => _active && IsDetails && !IsLoadingDetails && _canEdit());
        BackCommand = new RelayCommand(() => { SelectedResult = null; IsDetails = false; Error = ""; }, () => _canEdit());
        CancelRequestCommand = new RelayCommand(() => { _search?.Cancel(); _detailsRequest?.Cancel(); Message = "Загрузка отменена. Можно повторить запрос."; }, () => IsBusy || IsLoadingDetails);
        OpenModrinthCommand = new RelayCommand(() => OpenUrl("https://modrinth.com"));
    }

    public void Open(bool packs, GameInstance? instance, string game)
    {
        Deactivate(); _contextGeneration++; _active = true; IsPacks = packs; _instance = instance; _game = game;
        _offset = 0; _total = 0; PlanText = ""; _plannedVersion = null; SelectedResult = null; IsDetails = false; Installed.Clear(); SelectedInstalled = null;
        OnPropertyChanged(nameof(HasNoInstalled));
        OnPropertyChanged(nameof(Context)); OnPropertyChanged(nameof(IsMods)); OnPropertyChanged(nameof(SearchTitle));
        if (!packs && (instance is null || instance.Loader == ModLoader.Vanilla))
        { Error = "Vanilla не поддерживает моды. Создай Fabric, Forge или NeoForge сборку."; ClearResults(); RefreshCommands(); return; }
        _ = SearchAsync(false);
        if (!packs) _ = RefreshInstalledAsync();
    }
    public void Deactivate() { _active = false; _search?.Cancel(); _detailsRequest?.Cancel(); }
    partial void OnQueryChanged(string value) { _offset = 0; if (_active) _ = SearchAsync(true); }
    partial void OnSelectedResultChanged(ModrinthResultViewModel? value) { _plannedVersion = null; PlanText = ""; _ = LoadDetailsAsync(value); RefreshCommands(); }
    partial void OnSelectedVersionChanged(ModrinthVersion? value) { _plannedVersion = null; PlanText = ""; RefreshCommands(); }
    partial void OnSelectedInstalledChanged(InstalledMod? value) => RefreshCommands();
    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnPlanTextChanged(string value) => OnPropertyChanged(nameof(HasPlan));
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(HasNoResults)); RefreshCommands(); }
    partial void OnIsDetailsChanged(bool value) { OnPropertyChanged(nameof(IsBrowsing)); RefreshCommands(); }
    partial void OnIsLoadingDetailsChanged(bool value) => RefreshCommands();
    partial void OnDetailsChanged(ProjectDetailsViewModel? oldValue, ProjectDetailsViewModel? newValue)
    { oldValue?.Dispose(); OnPropertyChanged(nameof(HasDetails)); OnPropertyChanged(nameof(HasNoVersions)); }

    public void RefreshCommands()
    {
        SearchCommand?.NotifyCanExecuteChanged(); NextCommand?.NotifyCanExecuteChanged(); PreviousCommand?.NotifyCanExecuteChanged();
        PlanCommand?.NotifyCanExecuteChanged(); InstallCommand?.NotifyCanExecuteChanged(); RemoveCommand?.NotifyCanExecuteChanged();
        CheckUpdateCommand?.NotifyCanExecuteChanged(); RefreshInstalledCommand?.NotifyCanExecuteChanged();
        RetryDetailsCommand?.NotifyCanExecuteChanged(); CancelRequestCommand?.NotifyCanExecuteChanged(); BackCommand?.NotifyCanExecuteChanged(); ShowInstalledCommand?.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsEditable));
    }
    private void ClearResults() { foreach (var item in Results) item.Dispose(); Results.Clear(); OnPropertyChanged(nameof(HasNoResults)); }
    private async Task SearchAsync(bool debounce)
    {
        _search?.Cancel();
        using var current = new CancellationTokenSource(); _search = current;
        IsBusy = true; Error = "";
        try
        {
            if (debounce) await Task.Delay(400, current.Token);
            var result = await _api.SearchAsync(Query, IsPacks, _instance, _offset, current.Token);
            current.Token.ThrowIfCancellationRequested();
            if (_disposed || !_active || !ReferenceEquals(current, _search)) return;
            SelectedResult = null; ClearResults();
            foreach (var hit in result.Hits) Results.Add(new(hit));
            _total = result.TotalHits;
            Message = result.TotalHits == 0 ? "Ничего не найдено · Данные Modrinth" : $"{_offset + 1}–{_offset + result.Hits.Count} из {result.TotalHits} · Данные Modrinth";
            foreach (var item in Results.ToArray()) await LoadIconAsync(item, current.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (_active && !current.IsCancellationRequested && ReferenceEquals(current, _search)) SetError(ex); }
        finally { if (ReferenceEquals(current, _search)) { _search = null; IsBusy = false; } }
    }
    private async Task LoadIconAsync(ModrinthResultViewModel item, CancellationToken token)
    {
        var bitmap = await ReadIconAsync(item.Hit.IconUrl, token);
        if (!_disposed && !token.IsCancellationRequested && Results.Contains(item)) item.Icon = bitmap;
        else bitmap?.Dispose();
    }
    private async Task<Bitmap?> ReadIconAsync(string? url, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            var bytes = await _http.GetBytesAsync(url, ["cdn.modrinth.com"], token, 2 * 1024 * 1024);
            return await Task.Run(() => { using var stream = new MemoryStream(bytes); return Bitmap.DecodeToWidth(stream, 64); }, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Optional icon failure never blocks search or installation. */ }
        return null;
    }
    private async Task LoadDetailsAsync(ModrinthResultViewModel? item)
    {
        _detailsRequest?.Cancel(); _detailsRequest = null; Versions.Clear(); SelectedVersion = null; Details = null; IsLoadingDetails = false;
        if (item is null) { IsDetails = false; return; }
        _search?.Cancel();
        using var current = new CancellationTokenSource(); _detailsRequest = current;
        IsDetails = true; IsLoadingDetails = true; Error = ""; Message = "";
        var instance = IsPacks ? null : _instance;
        try
        {
            var project = await _api.ProjectAsync(item.Hit.ProjectId, current.Token);
            if (project.ProjectType != (IsPacks ? "modpack" : "mod")) throw new InvalidDataException("Тип проекта не соответствует выбранному разделу.");
            var versionsTask = _api.VersionsAsync(project.Id, instance, current.Token);
            var membersTask = MembersOrEmptyAsync(project.Id, current.Token);
            var descriptionTask = Task.Run(() =>
            {
                try { return (Document: ProjectDescriptionParser.Parse(project.Body, current.Token), Notice: ""); }
                catch (InvalidDataException ex) { return (Document: ProjectDescription.Empty, Notice: ex.Message); }
            }, current.Token);
            await Task.WhenAll(versionsTask, membersTask, descriptionTask);
            current.Token.ThrowIfCancellationRequested();
            if (_disposed || !_active || !ReferenceEquals(_detailsRequest, current)) return;
            // Keep the chooser safe even if a service implementation returns unfiltered results.
            foreach (var version in versionsTask.Result.Where(x => x.ProjectId == project.Id && (instance is null || ModrinthService.IsCompatible(x, instance)))
                .Where(x => x.Environment is null ? project.ClientSide is "required" or "optional" : ModrinthService.SupportsClient(x.Environment))
                .OrderByDescending(x => x.DatePublished)) Versions.Add(version);
            Details = new(project, descriptionTask.Result.Document, descriptionTask.Result.Notice, item.Hit.Author, membersTask.Result, OpenUrl);
            Details.UpdateInstalled(Installed, Versions);
            SelectedVersion = Versions.FirstOrDefault();
            OnPropertyChanged(nameof(HasNoVersions));
            if (IsPacks) PackName = project.Title[..Math.Min(60, project.Title.Length)];
            var icon = await ReadIconAsync(project.IconUrl, current.Token);
            if (ReferenceEquals(_detailsRequest, current) && !current.IsCancellationRequested && !_disposed) Details.Icon = icon;
            else icon?.Dispose();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (_active && !current.IsCancellationRequested && ReferenceEquals(_detailsRequest, current)) SetError(ex); }
        finally { if (ReferenceEquals(_detailsRequest, current)) { _detailsRequest = null; IsLoadingDetails = false; } RefreshCommands(); }
    }
    private async Task<System.Collections.Generic.IReadOnlyList<ModrinthTeamMember>> MembersOrEmptyAsync(string project, CancellationToken token)
    {
        try { return await _api.MembersAsync(project, token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _log("Modrinth: авторы недоступны. " + LauncherLog.Sanitize(ex.Message)); return Array.Empty<ModrinthTeamMember>(); }
    }
    private Task PlanAsync() => _run(async token =>
    {
        var version = SelectedVersion; var instance = _instance;
        if (version is null || instance is null) return;
        _plannedVersion = null; PlanText = ""; RefreshCommands();
        var generation = _contextGeneration;
        var plan = await _mods.PlanAsync(instance, _game, version.Id, token);
        if (_disposed || generation != _contextGeneration || SelectedVersion?.Id != version.Id) return;
        PlanText = "Будут установлены/проверены:\n" + string.Join("\n", plan.Projects.Select(x => "• " + x.DisplayName)) +
            (plan.Notices.Count == 0 ? "" : "\n\n" + string.Join("\n", plan.Notices)) + "\nРучные JAR не удаляются. Их совместимость проверь самостоятельно.";
        _plannedVersion = version.Id; RefreshCommands();
    });
    private Task InstallAsync() => _run(async token =>
    {
        var version = SelectedVersion; var project = Details?.Project;
        if (version is null || project is null) return;
        var generation = _contextGeneration;
        if (IsPacks)
        {
            await _packs.InstallAsync(project.Id, version.Id, PackName, _root(), IncludeOptionalPackFiles, _publish, _progress(), token);
            if (!_disposed && generation == _contextGeneration) Message = "Modpack установлен. Сборка доступна в разделе «Играть».";
        }
        else if (_instance is not null)
        {
            await _mods.InstallAsync(_instance, _game, version.Id, _progress(), token);
            if (_disposed || generation != _contextGeneration) return;
            await RefreshInstalledAsync(); PlanText = ""; _plannedVersion = null; Message = "Моды установлены.";
        }
    });
    private async Task RefreshInstalledAsync()
    {
        var game = _game;
        var context = _contextGeneration;
        var request = ++_installedGeneration;
        if (_instance is null) return;
        try
        {
            var installed = await _mods.LoadAsync(game, CancellationToken.None);
            if (_disposed || context != _contextGeneration || request != _installedGeneration) return;
            var selected = SelectedInstalled?.Version.ProjectId;
            Installed.Clear(); foreach (var mod in installed.Projects) Installed.Add(mod);
            SelectedInstalled = Installed.FirstOrDefault(x => x.Version.ProjectId == selected);
            Details?.UpdateInstalled(Installed, Versions); OnPropertyChanged(nameof(HasNoInstalled)); RefreshCommands();
        }
        catch (Exception ex) { if (!_disposed && context == _contextGeneration && request == _installedGeneration) SetError(ex); }
    }
    private Task RemoveAsync() => _run(async token =>
    {
        if (SelectedInstalled is not { } selected) return;
        var generation = _contextGeneration;
        await _mods.RemoveAsync(_game, selected.Version.ProjectId, token);
        if (_disposed || generation != _contextGeneration) return;
        await RefreshInstalledAsync(); Message = "Мод удалён. Его зависимости сохранены; ненужные можно удалить отдельно.";
    });
    private Task CheckUpdateAsync() => _run(async token =>
    {
        if (SelectedInstalled is not { } selected || _instance is null) return;
        var generation = _contextGeneration;
        var versions = await _api.VersionsAsync(selected.Version.ProjectId, _instance, token);
        if (_disposed || generation != _contextGeneration) return;
        var latest = versions.FirstOrDefault();
        if (latest is null || latest.Id == selected.Version.Id || latest.DatePublished <= selected.Version.DatePublished)
        { Message = "Совместимых обновлений не найдено."; return; }
        ShowInstalled(selected);
        // Let the ordinary details/version chooser load; updates use the same reviewed installation plan.
        Message = "Доступно обновление " + latest.VersionNumber + ". Выбери версию и нажми «Проверить зависимости», затем «Установить».";
    });
    private void ShowInstalled(InstalledMod selected) => SelectedResult = new(new ModrinthHit { ProjectId = selected.Version.ProjectId, Title = selected.Title, ProjectType = "mod" });
    private void SetError(Exception ex) { Error = LauncherLog.Sanitize(ex.Message); _log(ex.ToString()); }
    private void OpenUrl(string url)
    { if (!SafeProjectLink.TryCreate(url, out var uri)) return; try { Process.Start(new ProcessStartInfo(uri!.AbsoluteUri) { UseShellExecute = true }); } catch (Exception ex) { SetError(ex); } }
    public void Dispose() { _disposed = true; Deactivate(); Details = null; ClearResults(); }
}
