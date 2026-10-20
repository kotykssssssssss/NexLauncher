using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexLauncher.Models;
using NexLauncher.Services.Diagnostics;
using NexLauncher.Services.Modrinth;
using NexLauncher.Services.Sessions;
using NexLauncher.Services.Storage;

namespace NexLauncher.ViewModels;

public partial class NexDoctorViewModel : ObservableObject
{
    private readonly NexDoctorService doctor;
    private readonly SessionStore store;
    private readonly Func<GameInstance, DiagnosticIssue, CancellationToken, Task> fix;
    private readonly Func<Func<CancellationToken, Task>, Task> run;
    private readonly Func<bool> editable;
    private readonly Action<GameInstance> openMods;
    private readonly Action<string> openPath;
    private readonly Action<string> openSession;
    private readonly string dataDirectory;
    private long selectionGeneration;
    public ObservableCollection<GameInstance> Instances { get; } = new();
    public ObservableCollection<DiagnosticIssue> Issues { get; } = new();
    public ObservableCollection<DoctorReport> CrashHistory { get; } = new();
    [ObservableProperty] private GameInstance? _selectedInstance;
    [ObservableProperty] private DoctorReport? _selectedReport;
    [ObservableProperty] private DoctorReport? _selectedCrash;
    [ObservableProperty] private DiagnosticIssue? _selectedIssue;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _logFilter = "All";
    [ObservableProperty] private string _status = "Выбери сборку и запусти локальную проверку.";
    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private bool _isBusy;
    public string[] LogFilters { get; } = ["All", "ERROR", "WARN", "INFO"];
    public bool IsEditable => editable();
    public int Errors => Issues.Count(x => x.Severity == DiagnosticSeverity.Error);
    public int Warnings => Issues.Count(x => x.Severity == DiagnosticSeverity.Warning);
    public string Health => SelectedReport is null ? "Не проверено" : Errors > 0 ? "Problems detected" : Warnings > 0 ? "Warnings" : "Healthy";
    public string LastScan => SelectedReport?.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm") ?? "—";
    public bool HasSession => SelectedReport?.SessionId is not null;
    public bool NoIssues => SelectedReport is not null && Issues.Count == 0;
    public IAsyncRelayCommand ScanCommand { get; }
    public IAsyncRelayCommand FixSelectedCommand { get; }
    public IAsyncRelayCommand FixSafeCommand { get; }
    public IRelayCommand OpenLogCommand { get; }
    public IRelayCommand OpenFolderCommand { get; }
    public IRelayCommand OpenModsCommand { get; }
    public IRelayCommand OpenSessionCommand { get; }
    public IAsyncRelayCommand CopyCommand { get; }
    public IAsyncRelayCommand CopyLogCommand { get; }
    public Func<string, Task>? CopyTextAsync { get; set; }
    public NexDoctorViewModel(string dataDirectory, NexDoctorService doctor, SessionStore store,
        Func<Func<CancellationToken, Task>, Task> run, Func<bool> editable,
        Func<GameInstance, DiagnosticIssue, CancellationToken, Task> fix, Action<GameInstance> openMods, Action<string> openPath, Action<string> openSession)
    {
        this.dataDirectory = dataDirectory; this.doctor = doctor; this.store = store; this.run = run;
        this.editable = editable; this.fix = fix; this.openMods = openMods; this.openPath = openPath; this.openSession = openSession;
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => IsEditable && SelectedInstance is not null);
        FixSelectedCommand = new AsyncRelayCommand(() => FixAsync(false), () => IsEditable && SelectedIssue?.HasSafeFix == true);
        FixSafeCommand = new AsyncRelayCommand(() => FixAsync(true), () => IsEditable && Issues.Any(x => x.HasSafeFix));
        OpenLogCommand = new RelayCommand(() =>
        {
            if (SelectedReport?.LogPath is not { } path || SelectedInstance is not { } instance) return;
            var game = SafePaths.GamePath(dataDirectory, instance);
            // Persisted report paths are untrusted too: only expected source paths for this instance.
            var relative = Path.GetRelativePath(game, path).Replace('\\', '/');
            if (relative != "logs/latest.log" && !(relative.StartsWith("crash-reports/", StringComparison.Ordinal) && relative.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))) return;
            try { openPath(SafePaths.Resolve(game, relative)); } catch (Exception) { Status = "Исходный лог недоступен."; }
        }, () => SelectedReport?.LogPath is not null);
        OpenFolderCommand = new RelayCommand(() => { if (SelectedInstance is { } instance) openPath(SafePaths.GamePath(dataDirectory, instance)); }, () => SelectedInstance is not null);
        OpenModsCommand = new RelayCommand(() => { if (SelectedInstance is { } instance) openMods(instance); }, () => SelectedInstance is not null);
        OpenSessionCommand = new RelayCommand(() => { if (SelectedReport?.SessionId is { } id) openSession(id); }, () => HasSession);
        CopyCommand = new AsyncRelayCommand(async () =>
        {
            var details = SelectedIssue is { } issue ? issue.Title + "\n" + issue.Classification + "\n" + issue.Description + "\n" + issue.Evidence + "\n" + issue.Recommendation : LogText;
            await CopyAsync(details);
        });
        CopyLogCommand = new AsyncRelayCommand(() => CopyAsync(LogText));
    }
    private async Task CopyAsync(string text)
    {
        if (CopyTextAsync is null) { Status = "Буфер обмена недоступен."; return; }
        try { await CopyTextAsync(text); Status = "Скопировано локально. Перед публикацией проверь личные данные в тексте."; }
        catch (Exception) { Status = "Буфер обмена недоступен."; }
    }
    public void Configure(System.Collections.Generic.IEnumerable<GameInstance> instances, GameInstance? preferred)
    {
        var id = SelectedInstance?.Id ?? preferred?.Id; Instances.Clear(); foreach (var instance in instances) Instances.Add(instance);
        SelectedInstance = Instances.FirstOrDefault(x => x.Id == id) ?? Instances.FirstOrDefault();
    }
    public async Task OpenAsync(string? reportId = null)
    {
        var generation = ++selectionGeneration;
        try
        {
            var reports = await store.ReportsAsync();
            if (generation != selectionGeneration) return;
            var requested = reportId is null ? null : reports.FirstOrDefault(x => x.Id == reportId);
            if (requested is not null) SelectedInstance = Instances.FirstOrDefault(x => x.Id == requested.InstanceId);
            ++selectionGeneration; // Explicit navigation wins over an older default report load.
            CrashHistory.Clear(); foreach (var report in reports.Where(x => x.SessionId is not null).Take(200)) CrashHistory.Add(report);
            SelectedReport = reportId is not null ? requested : reports.FirstOrDefault(x => x.InstanceId == SelectedInstance?.Id);
            if (reportId is not null && requested is null) Status = "Отчёт больше не доступен: он мог быть удалён или выйти за лимит истории. Игровая сессия сохранена.";
            if (store.Warning.Length > 0) Status = store.Warning;
        }
        catch (Exception ex) { Status = "История диагностики недоступна: " + ex.GetType().Name; }
    }
    partial void OnSelectedInstanceChanged(GameInstance? value)
    {
        SelectedReport = null; RefreshPermissions();
        var generation = ++selectionGeneration;
        if (value is not null) _ = LoadLatestAsync(value.Id, generation);
    }
    private async Task LoadLatestAsync(string id, long generation)
    {
        try { var reports = await store.ReportsAsync(id); if (generation == selectionGeneration && SelectedInstance?.Id == id && SelectedReport is null) SelectedReport = reports.FirstOrDefault(); }
        catch (Exception) { Status = "Предыдущая диагностика недоступна."; }
    }
    partial void OnSelectedReportChanged(DoctorReport? value)
    {
        Issues.Clear(); if (value is not null) foreach (var issue in value.Issues) Issues.Add(issue);
        SelectedIssue = Issues.FirstOrDefault(); FilterLog();
        foreach (var property in new[] { nameof(Errors), nameof(Warnings), nameof(Health), nameof(LastScan), nameof(HasSession), nameof(NoIssues) }) OnPropertyChanged(property);
        RefreshPermissions();
    }
    partial void OnSelectedIssueChanged(DiagnosticIssue? value) => RefreshPermissions();
    partial void OnSelectedCrashChanged(DoctorReport? value)
    {
        if (value is null) return;
        SelectedInstance = Instances.FirstOrDefault(x => x.Id == value.InstanceId);
        SelectedReport = value;
    }
    partial void OnSearchChanged(string value) => FilterLog();
    partial void OnLogFilterChanged(string value) => FilterLog();
    private void FilterLog()
    {
        var lines = (SelectedReport?.LogExcerpt ?? "").Split('\n');
        LogText = string.Join("\n", lines.Where(x => (Search.Length == 0 || x.Contains(Search, StringComparison.OrdinalIgnoreCase)) &&
            (LogFilter == "All" || x.Contains(LogFilter, StringComparison.OrdinalIgnoreCase))));
    }
    private async Task ScanAsync()
    {
        if (SelectedInstance is not { } instance) return;
        await run(async token =>
        {
            IsBusy = true;
            try { SelectedReport = await doctor.ScanAsync(instance, SafePaths.GamePath(dataDirectory, instance), token); Status = "Проверка завершена. Healthy означает отсутствие известных проблем, а не гарантию отсутствия крашей."; }
            finally { IsBusy = false; }
        });
    }
    private async Task FixAsync(bool all)
    {
        if (SelectedInstance is not { } instance || SelectedReport?.InstanceId != instance.Id) return;
        var targets = all ? Issues.Where(x => x.HasSafeFix).ToArray() : SelectedIssue is { HasSafeFix: true } issue ? [issue] : Array.Empty<DiagnosticIssue>();
        await run(async token =>
        {
            foreach (var target in targets) { token.ThrowIfCancellationRequested(); await fix(instance, target, token); }
            SelectedReport = await doctor.ScanAsync(instance, SafePaths.GamePath(dataDirectory, instance), token);
            Status = "Безопасные действия выполнены; сборка проверена повторно.";
        });
    }
    public void RefreshPermissions()
    {
        OnPropertyChanged(nameof(IsEditable)); ScanCommand?.NotifyCanExecuteChanged(); FixSelectedCommand?.NotifyCanExecuteChanged(); FixSafeCommand?.NotifyCanExecuteChanged();
        OpenLogCommand?.NotifyCanExecuteChanged(); OpenFolderCommand?.NotifyCanExecuteChanged(); OpenModsCommand?.NotifyCanExecuteChanged(); OpenSessionCommand?.NotifyCanExecuteChanged();
    }
}
