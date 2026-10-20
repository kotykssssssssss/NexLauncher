using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NexLauncher.Models;
using NexLauncher.Services.Sessions;

namespace NexLauncher.Services.Diagnostics;

public sealed class NexDoctorService(InstanceHealthService health, SessionStore store)
{
    public async Task<DoctorReport> ScanAsync(GameInstance instance, string game, CancellationToken token)
    {
        var report = new DoctorReport { InstanceId = instance.Id, InstanceName = instance.Name,
            Issues = await health.ScanAsync(instance, game, token).ConfigureAwait(false) };
        try
        {
            var log = await DoctorLogReader.ReadAsync(game, DateTimeOffset.UtcNow.AddDays(-7), token).ConfigureAwait(false);
            report.LogPath = log.Path; report.LogExcerpt = log.Text;
            // Manual scan displays logs but does not classify old exceptions as a new crash.
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        { report.Issues.Add(new("log-unavailable", DiagnosticSeverity.Info, DiagnosticConfidence.Confirmed, "Лог недоступен", "Проверка файлов выполнена без лога.", LauncherLog.Sanitize(ex.Message), "Открой папку сборки.")); }
        await store.SaveReportAsync(report, token).ConfigureAwait(false); return report;
    }
    public async Task<DoctorReport> DiagnoseAsync(GameSession session, CancellationToken token)
    {
        var report = new DoctorReport { InstanceId = session.InstanceId, InstanceName = session.InstanceName, SessionId = session.Id, ExitCode = session.ExitCode };
        if (session.Outcome == SessionOutcome.FailedToStart)
        {
            report.Issues.Add(new("launch-preparation", DiagnosticSeverity.Error, DiagnosticConfidence.Unknown, "Не удалось подготовить запуск",
                "Процесс Minecraft не был запущен. Точная причина доступна в журнале лаунчера; игрового лога может ещё не быть.",
                session.FailureCategory ?? "Launch preparation failed", "Открой журнал лаунчера, проверь сеть, Java и незавершённые операции с модами."));
            await store.SaveReportAsync(report, token).ConfigureAwait(false); return report;
        }
        try
        {
            var log = await DoctorLogReader.ReadAsync(session.GameDirectory, session.StartedAt ?? session.AttemptedAt, token).ConfigureAwait(false);
            report.LogPath = log.Path; report.LogExcerpt = log.Text;
            report.Issues = CrashRules.Analyze(log.Text, true);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { report.Issues = CrashRules.Analyze("", true); report.Issues[0] = report.Issues[0] with { Evidence = LauncherLog.Sanitize(ex.Message) }; }
        await store.SaveReportAsync(report, token).ConfigureAwait(false); return report;
    }
}
