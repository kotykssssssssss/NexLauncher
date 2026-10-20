using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Linq;

namespace NexLauncher.Models;

public enum SessionOutcome { Running, Completed, Crashed, FailedToStart, Cancelled, Interrupted }
public sealed class GameSession
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string InstanceId { get; set; } = "";
    public string InstanceName { get; set; } = "";
    public string MinecraftVersion { get; set; } = "";
    public ModLoader Loader { get; set; }
    public DateTimeOffset AttemptedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public DateTimeOffset LastObservedAt { get; set; }
    public double DurationSeconds { get; set; }
    public int? ExitCode { get; set; }
    public SessionOutcome Outcome { get; set; }
    public string? DiagnosticId { get; set; }
    public string? FailureCategory { get; set; }
    public int? ProcessId { get; set; }
    public DateTimeOffset? ProcessStartedAt { get; set; }
    public string GameDirectory { get; set; } = ""; // Internal only; never included in statistics exports.
    public long PeakRamBytes { get; set; }
    public double AverageRamBytes { get; set; }
    public long SampleCount { get; set; }
    public double AverageCpuPercent { get; set; }
    public long CpuSampleCount { get; set; }
    public List<PerformanceSample> Samples { get; set; } = new();
    [JsonIgnore] public string Label => $"{InstanceName} · {(StartedAt ?? AttemptedAt).ToLocalTime():dd.MM HH:mm} · {SessionFormatting.Duration(DurationSeconds)} · {Outcome}";
    public GameSession Copy(bool includeSamples = true)
    {
        var result = (GameSession)MemberwiseClone();
        result.Samples = includeSamples ? Samples.ToList() : new(); return result;
    }
}
public sealed record PerformanceSample(double Seconds, long RamBytes, double? CpuPercent);
public static class SessionFormatting
{
    public static string Duration(double seconds) => seconds >= 3600 ? $"{seconds / 3600:0.0} ч" : $"{seconds / 60:0.0} мин";
}
