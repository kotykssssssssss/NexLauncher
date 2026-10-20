using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NexLauncher.Models;
using NexLauncher.Services.Storage;

namespace NexLauncher.Services.Sessions;

/// <summary>Atomic per-record files with in-memory ID/instance indexes. Corrupt originals are retained.</summary>
public sealed class SessionStore
{
    public const int MaximumSessions = 10000;
    public const int MaximumReports = 512;
    private readonly string root;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, GameSession> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, System.Collections.Generic.HashSet<string>> instanceSessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DoctorReport> reports = new(StringComparer.Ordinal);
    private bool loaded;
    public string Warning { get; private set; } = "";
    public event Action? Changed;
    public SessionStore(string dataDirectory) => root = SafePaths.Resolve(dataDirectory, "insights");
    private string RecordPath(string kind, string id)
    {
        if (!Guid.TryParseExact(id, "N", out var guid) || guid == Guid.Empty) throw new InvalidDataException("Некорректный ID истории.");
        return SafePaths.Resolve(root, kind + "/" + id + ".json");
    }
    private static T Copy<T>(T value) => value switch
    {
        GameSession s => (T)(object)s.Copy(),
        DoctorReport r => (T)(object)new DoctorReport { SchemaVersion = r.SchemaVersion, Id = r.Id, InstanceId = r.InstanceId, InstanceName = r.InstanceName,
            SessionId = r.SessionId, CreatedAt = r.CreatedAt, ExitCode = r.ExitCode, Issues = r.Issues.ToList(), LogPath = r.LogPath, LogExcerpt = r.LogExcerpt },
        _ => throw new ArgumentException("Unknown history record")
    };
    public async Task InitializeAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (loaded) return;
            sessions.Clear(); reports.Clear(); instanceSessions.Clear();
            await Task.Run(async () =>
            {
                await LoadKindAsync<GameSession>("sessions", s => { Validate(s); sessions.Add(s.Id, s.Copy(false)); Index(s); }, token).ConfigureAwait(false);
                await LoadKindAsync<DoctorReport>("reports", r =>
                {
                    ValidateReport(r);
                    _ = RecordPath("reports", r.Id); reports.Add(r.Id, r);
                }, token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            loaded = true;
        }
        finally { gate.Release(); }
    }
    private async Task LoadKindAsync<T>(string kind, Action<T> accept, CancellationToken token)
    {
        var folder = SafePaths.Resolve(root, kind);
        if (!Directory.Exists(folder)) return;
        SafePaths.NoLinks(folder);
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
        {
            token.ThrowIfCancellationRequested();
            if (++count > (kind == "reports" ? MaximumReports : MaximumSessions)) { Warning = "История достигла лимита. Экспортируй и очисти её для продолжения записи."; break; }
            try
            {
                var item = await AtomicJson.ReadAsync<T>(path, token).ConfigureAwait(false);
                var id = Path.GetFileNameWithoutExtension(path);
                _ = RecordPath(kind, id);
                if (item is GameSession s && s.Id != id || item is DoctorReport r && r.Id != id) throw new InvalidDataException("ID не совпадает с именем файла.");
                accept(item!);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NullReferenceException)
            { Warning = "Некоторые записи истории повреждены или имеют неизвестный формат. Оригиналы сохранены в insights."; }
        }
    }
    public static void Validate(GameSession s)
    {
        if (s.SchemaVersion is < 0 or > 1 || !Guid.TryParseExact(s.Id, "N", out var sessionId) || sessionId == Guid.Empty || !Guid.TryParse(s.InstanceId, out var instanceId) || instanceId == Guid.Empty ||
            s.InstanceName is null || s.InstanceName.Length > 200 || s.MinecraftVersion is null || s.Samples is null || s.Samples.Count > PerformanceAccumulator.MaximumSamples ||
            !double.IsFinite(s.DurationSeconds) || s.DurationSeconds < 0 || s.DurationSeconds > TimeSpan.FromDays(3650).TotalSeconds || !Enum.IsDefined(s.Outcome) || !Enum.IsDefined(s.Loader) ||
            !double.IsFinite(s.AverageRamBytes) || s.AverageRamBytes < 0 || !double.IsFinite(s.AverageCpuPercent) || s.AverageCpuPercent is < 0 or > 100 || s.PeakRamBytes < 0 || s.SampleCount < 0 || s.CpuSampleCount < 0 ||
            s.Samples.Any(x => x is null || !double.IsFinite(x.Seconds) || x.Seconds < 0 || x.RamBytes < 0 || x.CpuPercent is { } cpu && (!double.IsFinite(cpu) || cpu < 0 || cpu > 100)))
            throw new InvalidDataException("Повреждена запись сессии. Оригинал сохранён.");
        if (s.ProcessStartedAt is not null && (s.ProcessId is null or <= 0 || s.StartedAt is null))
            throw new InvalidDataException("Повреждена идентичность игрового процесса. Оригинал сохранён.");
        if (s.StartedAt is { } started)
        {
            try { _ = started.AddSeconds(s.DurationSeconds); }
            catch (ArgumentOutOfRangeException) { throw new InvalidDataException("Длительность выходит за диапазон даты сессии. Оригинал сохранён."); }
        }
        // Version 0 had no performance data; missing fields keep their conservative defaults.
        s.SchemaVersion = 1;
        s.InstanceId = instanceId.ToString("N");
    }
    public async Task SaveAsync(GameSession session, CancellationToken token = default)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        Validate(session);
        var copy = Copy(session); Validate(copy);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!sessions.ContainsKey(copy.Id) && sessions.Count >= MaximumSessions) throw new IOException("История достигла 10 000 сессий. Экспортируй и очисти её.");
            var original = await AtomicJson.ReadAsync<GameSession>(RecordPath("sessions", copy.Id), token).ConfigureAwait(false);
            if (original is not null)
            {
                Validate(original);
                if (original.Id != copy.Id || original.InstanceId != copy.InstanceId || original.AttemptedAt != copy.AttemptedAt)
                    throw new InvalidDataException("Идентичность исходной сессии изменена. Оригинал сохранён.");
            }
            await AtomicJson.WriteAsync(RecordPath("sessions", copy.Id), copy, token).ConfigureAwait(false);
            sessions[copy.Id] = copy.Copy(false); Index(copy);
        }
        finally { gate.Release(); }
        Changed?.Invoke();
    }
    public async Task SaveReportAsync(DoctorReport report, CancellationToken token = default)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ValidateReport(report); var copy = Copy(report);
            var existing = await AtomicJson.ReadAsync<DoctorReport>(RecordPath("reports", copy.Id), token).ConfigureAwait(false);
            if (existing is not null) { ValidateReport(existing); if (existing.Id != copy.Id) throw new InvalidDataException("ID исходного отчёта изменён. Оригинал сохранён."); }
            await AtomicJson.WriteAsync(RecordPath("reports", copy.Id), copy, token).ConfigureAwait(false); reports[copy.Id] = copy;
            foreach (var expired in reports.Values.OrderBy(x => x.SessionId is not null).ThenBy(x => x.CreatedAt).Take(Math.Max(0, reports.Count - MaximumReports)).ToArray())
            {
                var path = RecordPath("reports", expired.Id);
                try
                {
                    var original = await AtomicJson.ReadAsync<DoctorReport>(path, CancellationToken.None).ConfigureAwait(false);
                    if (original is not null) { ValidateReport(original); if (original.Id == expired.Id) File.Delete(path); }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { Warning = "Старый отчёт не удалось удалить; оригинал сохранён."; }
                reports.Remove(expired.Id);
            }
        }
        finally { gate.Release(); }
        Changed?.Invoke();
    }
    public async Task<GameSession[]> SessionsAsync(string? instanceId = null, CancellationToken token = default)
    {
        await InitializeAsync(token).ConfigureAwait(false); await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => (instanceId is null ? sessions.Values.AsEnumerable() :
                instanceSessions.TryGetValue(instanceId, out var ids) ? ids.Select(id => sessions[id]) : Enumerable.Empty<GameSession>())
                .OrderByDescending(x => x.AttemptedAt).Select(Copy).ToArray(), token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
    public async Task<DoctorReport[]> ReportsAsync(string? instanceId = null, CancellationToken token = default)
    {
        await InitializeAsync(token).ConfigureAwait(false); await gate.WaitAsync(token).ConfigureAwait(false);
        try { return await Task.Run(() => reports.Values.Where(x => instanceId is null || x.InstanceId == instanceId).OrderByDescending(x => x.CreatedAt).Select(Copy).ToArray(), token).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    public async Task<GameSession?> SessionAsync(string id, CancellationToken token = default)
    {
        await InitializeAsync(token).ConfigureAwait(false); await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!sessions.ContainsKey(id)) return null;
            return await Task.Run(async () =>
            {
                var value = await AtomicJson.ReadAsync<GameSession>(RecordPath("sessions", id), token).ConfigureAwait(false);
                if (value is not null)
                {
                    Validate(value);
                    if (value.Id != id || value.InstanceId != sessions[id].InstanceId) throw new InvalidDataException("ID записи изменился. Оригинал сохранён.");
                }
                return value;
            }, token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
    public async Task ClearAsync(CancellationToken token)
    {
        await InitializeAsync(token).ConfigureAwait(false); await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // Only validated records owned by this store. Corrupt/unknown originals are never discarded.
            token.ThrowIfCancellationRequested();
            await Task.Run(async () =>
            {
                foreach (var id in sessions.Keys.ToArray())
                {
                    try
                    {
                        var original = await AtomicJson.ReadAsync<GameSession>(RecordPath("sessions", id), CancellationToken.None).ConfigureAwait(false);
                        if (original is not null) { Validate(original); if (original.Id != id) throw new InvalidDataException("ID записи изменён."); }
                        File.Delete(RecordPath("sessions", id));
                    }
                    catch (InvalidDataException) { Warning = "Повреждённые оригиналы истории сохранены."; }
                    instanceSessions[sessions[id].InstanceId].Remove(id); sessions.Remove(id);
                }
                foreach (var id in reports.Keys.ToArray())
                {
                    try
                    {
                        var original = await AtomicJson.ReadAsync<DoctorReport>(RecordPath("reports", id), CancellationToken.None).ConfigureAwait(false);
                        if (original is not null) { ValidateReport(original); if (original.Id != id) throw new InvalidDataException("ID отчёта изменён."); }
                        File.Delete(RecordPath("reports", id));
                    }
                    catch (InvalidDataException) { Warning = "Повреждённые оригиналы истории сохранены."; }
                    reports.Remove(id);
                }
            }).ConfigureAwait(false);
        }
        finally { gate.Release(); Changed?.Invoke(); }
    }
    private void Index(GameSession session)
    {
        if (!instanceSessions.TryGetValue(session.InstanceId, out var ids)) instanceSessions[session.InstanceId] = ids = new(StringComparer.Ordinal);
        ids.Add(session.Id);
    }
    private static void ValidateReport(DoctorReport report)
    {
        if (report.SchemaVersion != 1 || !Guid.TryParseExact(report.Id, "N", out _) || !Guid.TryParse(report.InstanceId, out _) ||
            report.InstanceName is null || report.Issues is null || report.Issues.Count > 512 || report.LogExcerpt is null || report.LogExcerpt.Length > 262144 ||
            report.Issues.Any(i => i is null || i.Code is null || i.Title is null || i.Description is null || i.Evidence is null || i.Recommendation is null ||
                !Enum.IsDefined(i.Severity) || !Enum.IsDefined(i.Confidence) || !Enum.IsDefined(i.Fix)))
            throw new InvalidDataException("Повреждённый или неизвестный формат отчёта. Оригинал сохранён.");
    }
}
