using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexLauncher.Models;
using NexLauncher.Services.Sessions;

namespace NexLauncher.ViewModels;

public sealed record StatsInstanceChoice(string? Id, string Name) { public override string ToString() => Name; }
public partial class NexStatsViewModel : ObservableObject, IDisposable
{
    private readonly SessionStore store;
    private readonly Func<bool> canClear;
    private readonly Action<string> openDiagnosis;
    private readonly Dictionary<string, string> names = new();
    private CancellationTokenSource? refresh;
    private bool disposed;
    private bool active;
    private GameSession[] filtered = [];
    private bool lastRefreshSucceeded;
    public ObservableCollection<StatsInstanceChoice> Instances { get; } = new();
    public ObservableCollection<GameSession> Sessions { get; } = new();
    public string[] Periods { get; } = ["Last 7 Days", "Last 30 Days", "Last 90 Days", "All Time", "Custom"];
    public string[] Buckets { get; } = ["Daily", "Weekly", "Monthly"];
    public string[] RecapPeriods { get; } = ["This Month", "This Year", "All Time"];
    [ObservableProperty] private StatsInstanceChoice? _selectedInstance;
    [ObservableProperty] private string _period = "Last 30 Days";
    [ObservableProperty] private string _bucket = "Daily";
    [ObservableProperty] private string _fromDate = DateTime.Today.AddDays(-30).ToString("yyyy-MM-dd");
    [ObservableProperty] private string _toDate = DateTime.Today.ToString("yyyy-MM-dd");
    [ObservableProperty] private string _recapPeriod = "This Month";
    [ObservableProperty] private bool _isRecap;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _confirmClear;
    [ObservableProperty] private string _status = "История появится после запуска Minecraft через NexLauncher.";
    [ObservableProperty] private StatsSummary _summary = SessionAnalytics.Calculate([], new());
    [ObservableProperty] private GameSession? _selectedSession;
    [ObservableProperty] private ChartPoint[] _memory = [];
    public bool IsCustom => !IsRecap && Period == "Custom";
    public bool IsEmpty => Summary.TotalLaunches == 0;
    public bool HasData => !IsEmpty;
    public string Playtime => SessionFormatting.Duration(Summary.PlaytimeSeconds);
    public string Average => SessionFormatting.Duration(Summary.AverageSeconds);
    public string Longest => SessionFormatting.Duration(Summary.LongestSeconds);
    public string CrashRate => Summary.KnownOutcomes == 0 ? "—" : $"{Summary.CrashRate:0.0}%";
    public string LastPlayed => Summary.LastPlayed?.ToLocalTime().ToString("dd.MM.yyyy HH:mm") ?? "—";
    public string SessionDetails => SelectedSession is { } s ?
        $"{s.MinecraftVersion} · {s.Loader} · {s.Outcome} · exit {s.ExitCode?.ToString() ?? "unknown"}\n" +
        (s.SampleCount == 0 ? "Performance metrics не собирались для этой сессии." :
        $"RAM avg {s.AverageRamBytes / 1048576:0} MiB · peak {s.PeakRamBytes / 1048576:0} MiB · CPU avg {(s.CpuSampleCount == 0 ? "—" : s.AverageCpuPercent.ToString("0.0") + "%")} · samples {s.SampleCount}") : "Выбери сессию для просмотра RAM.";
    public bool HasDiagnosis => SelectedSession?.DiagnosticId is not null;
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand<string> ExportCommand { get; }
    public IAsyncRelayCommand ClearCommand { get; }
    public IRelayCommand OpenDiagnosisCommand { get; }
    public Func<string, Task<string?>>? SaveExportAsync { get; set; }
    public NexStatsViewModel(SessionStore store, Func<bool> canClear, Action<string> openDiagnosis)
    {
        this.store = store; this.canClear = canClear; this.openDiagnosis = openDiagnosis;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ExportCommand = new AsyncRelayCommand<string>(ExportAsync);
        ClearCommand = new AsyncRelayCommand(async () =>
        {
            if (!ConfirmClear || !canClear()) return;
            try { await store.ClearAsync(CancellationToken.None); ConfirmClear = false; await RefreshAsync(); Status = "История удалена. Сборки и аккаунты сохранены."; }
            catch (Exception ex) { Status = "Не удалось очистить историю: " + ex.GetType().Name; }
        }, () => ConfirmClear && canClear() && !IsBusy);
        OpenDiagnosisCommand = new RelayCommand(() => { if (SelectedSession?.DiagnosticId is { } id) openDiagnosis(id); }, () => HasDiagnosis);
        store.Changed += StoreChanged;
    }
    public void Configure(IEnumerable<GameInstance> instances)
    {
        var id = SelectedInstance?.Id; names.Clear(); Instances.Clear(); Instances.Add(new(null, "All Instances"));
        foreach (var instance in instances) { names[instance.Id] = instance.Name; Instances.Add(new(instance.Id, instance.Name)); }
        SelectedInstance = Instances.FirstOrDefault(x => x.Id == id) ?? Instances[0];
    }
    public void Open() { active = true; _ = RefreshAsync(); }
    public void Deactivate() { active = false; refresh?.Cancel(); }
    private void StoreChanged() { if (active && !disposed) Dispatcher.UIThread.Post(() => { if (active) _ = RefreshAsync(); }); }
    partial void OnSelectedInstanceChanged(StatsInstanceChoice? value) { if (active) _ = RefreshAsync(); }
    partial void OnPeriodChanged(string value) { OnPropertyChanged(nameof(IsCustom)); if (active) _ = RefreshAsync(); }
    partial void OnBucketChanged(string value) { if (active) _ = RefreshAsync(); }
    partial void OnRecapPeriodChanged(string value) { if (active) _ = RefreshAsync(); }
    partial void OnIsRecapChanged(bool value) { OnPropertyChanged(nameof(IsCustom)); if (active) _ = RefreshAsync(); }
    partial void OnConfirmClearChanged(bool value) => ClearCommand?.NotifyCanExecuteChanged();
    partial void OnSelectedSessionChanged(GameSession? value)
    {
        Memory = value?.Samples.Select(x => new ChartPoint($"{x.Seconds / 60:0.0} мин", x.RamBytes / 1048576d)).ToArray() ?? [];
        if (value is not null) _ = LoadMemoryAsync(value.Id);
        OnPropertyChanged(nameof(SessionDetails)); OnPropertyChanged(nameof(HasDiagnosis)); OpenDiagnosisCommand?.NotifyCanExecuteChanged();
    }
    private async Task LoadMemoryAsync(string id)
    {
        try
        {
            var session = await store.SessionAsync(id);
            if (disposed || SelectedSession?.Id != id) return;
            Memory = session?.Samples.Select(x => new ChartPoint($"{x.Seconds / 60:0.0} мин", x.RamBytes / 1048576d)).ToArray() ?? [];
        }
        catch (Exception) { if (SelectedSession?.Id == id) Status = "Samples этой сессии недоступны; исходная запись сохранена."; }
    }
    private StatsQuery Query()
    {
        var today = new DateTimeOffset(DateTime.Today);
        DateTimeOffset? from = null, until = null;
        if (IsRecap)
            from = RecapPeriod switch { "This Month" => new DateTimeOffset(new DateTime(today.Year, today.Month, 1)), "This Year" => new DateTimeOffset(new DateTime(today.Year, 1, 1)), _ => null };
        else if (Period == "Custom")
        {
            if (!DateTime.TryParseExact(FromDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
                !DateTime.TryParseExact(ToDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end) || end < start)
                throw new ArgumentException("Даты: yyyy-MM-dd, конец не раньше начала.");
            from = new DateTimeOffset(start); until = new DateTimeOffset(end.AddDays(1));
        }
        else from = Period switch { "Last 7 Days" => today.AddDays(-6), "Last 30 Days" => today.AddDays(-29), "Last 90 Days" => today.AddDays(-89), _ => null };
        return new(SelectedInstance?.Id, from, until, Bucket);
    }
    public async Task RefreshAsync()
    {
        if (disposed) return;
        refresh?.Cancel(); var cancellation = refresh = new CancellationTokenSource(); IsBusy = true; lastRefreshSucceeded = false;
        try
        {
            var query = Query(); var currentNames = new Dictionary<string, string>(names);
            var all = await store.SessionsAsync(token: cancellation.Token);
            var result = await Task.Run(() => (Rows: SessionAnalytics.Filter(all, query), Summary: SessionAnalytics.Calculate(all, query, currentNames)), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            var selected = SelectedSession?.Id; filtered = result.Rows; Summary = result.Summary; lastRefreshSucceeded = true;
            Sessions.Clear(); foreach (var session in filtered.Reverse().Take(500)) Sessions.Add(session);
            SelectedSession = Sessions.FirstOrDefault(x => x.Id == selected) ?? Sessions.FirstOrDefault();
            Status = store.Warning.Length > 0 ? store.Warning : "Учитываются запуски через NexLauncher. Незавершённые сессии не считаются подтверждёнными крашами. Графики: до 120 последних точек.";
            foreach (var property in new[] { nameof(Playtime), nameof(Average), nameof(Longest), nameof(CrashRate), nameof(LastPlayed), nameof(IsEmpty), nameof(HasData) }) OnPropertyChanged(property);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status = ex is ArgumentException ? ex.Message : "История недоступна: " + ex.GetType().Name; }
        finally { if (ReferenceEquals(refresh, cancellation)) { refresh = null; IsBusy = false; } cancellation.Dispose(); ClearCommand.NotifyCanExecuteChanged(); }
    }
    private async Task ExportAsync(string? format)
    {
        if (SaveExportAsync is null || format is not ("json" or "csv" or "png")) return;
        try
        {
            await RefreshAsync(); if (!lastRefreshSucceeded) return;
            var path = await SaveExportAsync(format); if (path is null) return;
            var rows = filtered.ToArray(); var summary = Summary; var period = IsRecap ? RecapPeriod : Period;
            var bytes = await Task.Run(() => format == "png" ? StatsExport.RecapPng(summary, period) : Encoding.UTF8.GetBytes(format == "csv" ? StatsExport.Csv(rows) : StatsExport.Json(rows)));
            await StatsExport.WriteAsync(path, bytes, CancellationToken.None); Status = "Экспорт сохранён без account data и локальных путей.";
        }
        catch (Exception ex) { Status = "Не удалось экспортировать статистику: " + ex.GetType().Name; }
    }
    public async Task ShowSessionAsync(string id)
    {
        try
        {
            Period = "All Time"; IsRecap = false; SelectedInstance = Instances.FirstOrDefault(x => x.Id is null);
            await RefreshAsync(); SelectedSession = Sessions.FirstOrDefault(x => x.Id == id);
            if (SelectedSession is null) SelectedSession = (await store.SessionsAsync()).FirstOrDefault(x => x.Id == id);
            if (SelectedSession is null) Status = "Сессия больше не доступна: история могла быть удалена.";
        }
        catch (Exception) { SelectedSession = null; Status = "Сессия недоступна. Исходные записи сохранены."; }
    }
    public void RefreshPermissions() => ClearCommand.NotifyCanExecuteChanged();
    public void Dispose() { disposed = true; store.Changed -= StoreChanged; refresh?.Cancel(); }
}
