using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NexLauncher.Models;

public enum DiagnosticSeverity { Info, Warning, Error }
public enum DiagnosticConfidence { Unknown, Possible, Likely, Confirmed }
public enum DiagnosticFix { None, RepairGame, RestoreMissingManagedFile, AutomaticJava, OpenFolder, OpenMods }
public sealed record DiagnosticIssue(string Code, DiagnosticSeverity Severity, DiagnosticConfidence Confidence,
    string Title, string Description, string Evidence, string Recommendation, DiagnosticFix Fix = DiagnosticFix.None,
    string? ProjectId = null)
{
    [JsonIgnore] public string Classification => $"{Severity} · {Confidence}";
    [JsonIgnore] public bool HasSafeFix => Fix is DiagnosticFix.RepairGame or DiagnosticFix.RestoreMissingManagedFile or DiagnosticFix.AutomaticJava;
}
public sealed class DoctorReport
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string InstanceId { get; set; } = "";
    public string InstanceName { get; set; } = "";
    public string? SessionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public int? ExitCode { get; set; }
    public List<DiagnosticIssue> Issues { get; set; } = new();
    public string? LogPath { get; set; }
    public string LogExcerpt { get; set; } = "";
    [JsonIgnore] public string Label => $"{CreatedAt.ToLocalTime():dd.MM.yyyy HH:mm} · {InstanceName} · exit {ExitCode?.ToString() ?? "—"}";
    [JsonIgnore] public string Summary => Issues.Count == 0 ? "Нет найденных проблем" : Issues[0].Title + " · " + Issues[0].Confidence;
}
