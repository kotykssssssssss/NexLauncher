using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NexLauncher.Models;
using NexLauncher.Services.Diagnostics;

namespace NexLauncher.Services.Sessions;

/// <summary>Optional observer. No observer failure is allowed to prevent a game launch.</summary>
public sealed class GameSessionTracker(SessionStore store, NexDoctorService doctor, LauncherLog log) : IDisposable
{
    private readonly ConcurrentDictionary<string, TrackedGameSession> active = new();
    private readonly CancellationTokenSource shutdown = new();
    public bool MonitorPerformance { get; set; }
    public bool HasActiveSession => !active.IsEmpty;
    public bool HasRunningProcess => System.Linq.Enumerable.Any(active.Values, x => x.Session.StartedAt is not null && x.Session.Outcome == SessionOutcome.Running);
    public event Action? ActiveProcessesChanged;
    public event Action<DoctorReport>? CrashDetected;
    public async Task<TrackedGameSession> BeginAsync(GameInstance instance, string game)
    {
        var session = new GameSession { InstanceId = instance.Id, InstanceName = instance.Name, MinecraftVersion = instance.VersionId,
            Loader = instance.Loader, GameDirectory = game, AttemptedAt = DateTimeOffset.UtcNow, LastObservedAt = DateTimeOffset.UtcNow };
        var tracking = new TrackedGameSession(this, session); active[session.Id] = tracking;
        await SaveAsync(session).ConfigureAwait(false); return tracking;
    }
    internal async Task SaveAsync(GameSession session)
    {
        try { await store.SaveAsync(session).ConfigureAwait(false); }
        catch (Exception ex) { log.Write("NexStats: запись недоступна: " + ex.GetType().Name); }
    }
    internal async Task FinalizeAsync(GameSession session)
    {
        try
        {
            if (session.Outcome is SessionOutcome.Crashed or SessionOutcome.FailedToStart)
            {
                var report = await doctor.DiagnoseAsync(session, CancellationToken.None).ConfigureAwait(false);
                session.DiagnosticId = report.Id;
                if (session.Outcome == SessionOutcome.Crashed)
                    try { CrashDetected?.Invoke(report); } catch (Exception ex) { log.Write("NexDoctor notification: " + ex.GetType().Name); }
            }
        }
        catch (Exception ex) { log.Write("NexDoctor: диагностика недоступна: " + ex.GetType().Name); }
        await SaveAsync(session).ConfigureAwait(false); active.TryRemove(session.Id, out _);
        NotifyProcessesChanged();
    }
    public async Task RecoverAsync()
    {
        try
        {
            foreach (var summary in await store.SessionsAsync().ConfigureAwait(false))
            {
                if (summary.Outcome != SessionOutcome.Running || active.ContainsKey(summary.Id)) continue;
                var session = await store.SessionAsync(summary.Id).ConfigureAwait(false) ?? summary;
                Process? process = null;
                try
                {
                    if (session.ProcessId is { } pid && session.ProcessStartedAt is { } started)
                    {
                        process = Process.GetProcessById(pid);
                        // PID alone can be reused by another application.
                        if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != started.UtcTicks) { process.Dispose(); process = null; }
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { process?.Dispose(); process = null; }
                if (process is not null)
                {
                    var tracking = new TrackedGameSession(this, session); active[session.Id] = tracking;
                    tracking.AttachRecovered(process); _ = ObserveRecoveredAsync(tracking, process);
                    NotifyProcessesChanged();
                }
                else
                {
                    RecoverInterrupted(session); await SaveAsync(session).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) { log.Write("NexStats recovery: " + ex.GetType().Name); }
    }
    public static void RecoverInterrupted(GameSession session)
    {
        // Unknown exit is neither a successful launch nor a confirmed crash. Keep only observed playtime.
        session.Outcome = SessionOutcome.Interrupted; session.EndedAt = session.LastObservedAt;
        session.ExitCode = null;
    }
    private async Task ObserveRecoveredAsync(TrackedGameSession tracking, Process process)
    {
        using (process)
        {
            try { await process.WaitForExitAsync(shutdown.Token).ConfigureAwait(false); await tracking.FinishAsync(process.ExitCode, false).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception) { await tracking.FinishAsync(null, false).ConfigureAwait(false); }
        }
    }
    internal CancellationToken Shutdown => shutdown.Token;
    internal void NotifyProcessesChanged() { try { ActiveProcessesChanged?.Invoke(); } catch (Exception) { } }
    public void Dispose() { shutdown.Cancel(); } // Does not kill the game; persisted PID/start time enables recovery.
}

public sealed class TrackedGameSession
{
    private readonly GameSessionTracker owner;
    public GameSession Session { get; }
    private readonly Stopwatch lifetime = new();
    private readonly CancellationTokenSource stop = new();
    private Task monitor = Task.CompletedTask;
    private double recoveredDuration;
    private int finished;
    internal TrackedGameSession(GameSessionTracker owner, GameSession session) { this.owner = owner; Session = session; }
    public async Task StartedAsync(Process process)
    {
        Session.StartedAt = DateTimeOffset.UtcNow;
        Session.ProcessId = process.Id;
        try { Session.ProcessStartedAt = new DateTimeOffset(process.StartTime.ToUniversalTime()); } catch (Exception) { }
        lifetime.Start(); StartMonitor(process); await owner.SaveAsync(Session).ConfigureAwait(false);
        owner.NotifyProcessesChanged();
    }
    internal void AttachRecovered(Process process)
    {
        // Verify process identity before trusting the interval spent while the launcher was closed.
        recoveredDuration = Math.Max(Session.DurationSeconds, (DateTimeOffset.UtcNow - Session.StartedAt!.Value).TotalSeconds);
        lifetime.Start(); StartMonitor(process);
    }
    private void StartMonitor(Process process) => monitor = Task.Run(async () =>
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, owner.Shutdown);
        var accumulator = new PerformanceAccumulator(Session);
        var ticks = 0;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellation.Token).ConfigureAwait(false);
                var elapsed = recoveredDuration + lifetime.Elapsed.TotalSeconds;
                Session.DurationSeconds = elapsed; Session.LastObservedAt = DateTimeOffset.UtcNow;
                if (owner.MonitorPerformance)
                {
                    try
                    {
                        process.Refresh(); accumulator.Add(elapsed, process.WorkingSet64, process.TotalProcessorTime.TotalSeconds, Environment.ProcessorCount);
                        Session.PeakRamBytes = Math.Max(Session.PeakRamBytes, process.PeakWorkingSet64);
                    }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }
                }
                if (++ticks % 6 == 0) await owner.SaveAsync(Session).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Monitoring is optional; never propagate to the game. */ }
    });
    public async Task FinishAsync(int? exitCode, bool cancelled)
    {
        if (Interlocked.Exchange(ref finished, 1) != 0) return;
        // Freeze process time before awaiting a pending checkpoint write. Slow storage must
        // not add launcher bookkeeping time to the finished game's duration or exit timestamp.
        lifetime.Stop(); var exitedAt = DateTimeOffset.UtcNow; stop.Cancel();
        await monitor.ConfigureAwait(false);
        Session.DurationSeconds = Session.StartedAt is null ? 0 : recoveredDuration + lifetime.Elapsed.TotalSeconds;
        Session.LastObservedAt = exitedAt; Session.EndedAt = exitedAt;
        Session.ExitCode = exitCode;
        Session.Outcome = Session.StartedAt is null ? (cancelled ? SessionOutcome.Cancelled : SessionOutcome.FailedToStart) :
            exitCode is null ? SessionOutcome.Interrupted : exitCode == 0 ? SessionOutcome.Completed : SessionOutcome.Crashed;
        if (Session.Outcome == SessionOutcome.Completed)
        {
            try
            {
                var log = await DoctorLogReader.ReadAsync(Session.GameDirectory, Session.StartedAt!.Value, CancellationToken.None).ConfigureAwait(false);
                if (log.HasFreshCrashReport) Session.Outcome = SessionOutcome.Crashed;
            }
            catch (Exception) { }
        }
        await owner.FinalizeAsync(Session).ConfigureAwait(false); stop.Dispose();
    }
}
