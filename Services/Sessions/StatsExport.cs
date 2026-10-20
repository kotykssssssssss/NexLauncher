using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NexLauncher.Models;
using NexLauncher.Services.Storage;
using SkiaSharp;

namespace NexLauncher.Services.Sessions;

public static class StatsExport
{
    public static string Json(GameSession[] sessions) => JsonSerializer.Serialize(new { schemaVersion = 1,
        sessions = sessions.Select(s => new { s.Id, s.InstanceId, s.InstanceName, s.MinecraftVersion, loader = s.Loader.ToString(), s.AttemptedAt,
            s.StartedAt, s.EndedAt, s.DurationSeconds, s.ExitCode, outcome = s.Outcome.ToString(), s.DiagnosticId,
            peakRamBytes = s.SampleCount == 0 ? (long?)null : s.PeakRamBytes, averageRamBytes = s.SampleCount == 0 ? (double?)null : s.AverageRamBytes,
            averageCpuPercent = s.CpuSampleCount == 0 ? (double?)null : s.AverageCpuPercent, s.SampleCount }) }, AtomicJson.Options);
    public static string Csv(GameSession[] sessions)
    {
        static string Field(string? value)
        {
            value ??= "";
            var probe = value.TrimStart();
            if (probe.Length > 0 && "=+-@".Contains(probe[0]) || value.Length > 0 && "\t\r\n".Contains(value[0]))
                value = "'" + value; // Spreadsheet formula injection, including leading whitespace.
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        static string Number(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        var text = new StringBuilder("session_id,instance_id,instance,minecraft,loader,start,end,duration_seconds,outcome,exit_code,peak_ram_bytes,average_ram_bytes,average_cpu_percent\r\n");
        foreach (var s in sessions)
            text.AppendLine(string.Join(",", new[] { s.Id, s.InstanceId, s.InstanceName, s.MinecraftVersion, s.Loader.ToString(), s.StartedAt?.ToString("O"), s.EndedAt?.ToString("O"), Number(s.DurationSeconds), s.Outcome.ToString(), s.ExitCode?.ToString(), s.SampleCount == 0 ? "" : s.PeakRamBytes.ToString(), s.SampleCount == 0 ? "" : Number(s.AverageRamBytes), s.CpuSampleCount == 0 ? "" : Number(s.AverageCpuPercent) }.Select(Field)));
        return text.ToString();
    }
    public static byte[] RecapPng(StatsSummary stats, string period)
    {
        using var surface = SKSurface.Create(new SKImageInfo(1200, 760)); var canvas = surface.Canvas;
        canvas.Clear(new SKColor(24, 28, 30));
        using var typeface = SKTypeface.FromFamilyName("Segoe UI");
        using var font = new SKFont(typeface, 28);
        using var paint = new SKPaint { IsAntialias = true, Color = SKColors.White };
        void Text(string value, float x, float y, float size, bool accent = false)
        { font.Size = size; paint.Color = accent ? new SKColor(120, 220, 186) : SKColors.White; canvas.DrawText(value, x, y, font, paint); }
        Text("NexStats / " + period, 56, 74, 26, true);
        Text(SessionFormatting.Duration(stats.PlaytimeSeconds), 56, 160, 64);
        Text("Minecraft playtime", 56, 210, 24);
        var cards = new[] { ("Sessions", stats.TotalLaunches.ToString()), ("Most played", stats.MostPlayedInstance),
            ("Favorite Minecraft", stats.FavoriteVersion), ("Longest session", SessionFormatting.Duration(stats.LongestSeconds)),
            ("Busiest day", stats.BusiestDay), ("Crashes", stats.Crashes.ToString()), ("Crash-free sessions in a row", stats.CrashFreeStreak.ToString()) };
        for (var i = 0; i < cards.Length; i++)
        {
            var x = 56 + i % 2 * 554; var y = 280 + i / 2 * 102;
            Text(cards[i].Item1, x, y, 20, true);
            var value = cards[i].Item2; font.Size = 28;
            if (font.MeasureText(value) > 494)
            {
                while (value.Length > 0 && font.MeasureText(value + "…") > 494) value = value[..^1];
                value += "…";
            }
            Text(value, x, y + 42, 28);
        }
        using var image = surface.Snapshot(); using var data = image.Encode(SKEncodedImageFormat.Png, 100); return data.ToArray();
    }
    public static async Task WriteAsync(string path, byte[] data, CancellationToken token)
    {
        SafePaths.NoLinks(path); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temporary, data, token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); SafePaths.NoLinks(path); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
