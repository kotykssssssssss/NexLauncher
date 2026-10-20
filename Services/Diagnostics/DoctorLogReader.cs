using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NexLauncher.Models;
using NexLauncher.Services.Storage;

namespace NexLauncher.Services.Diagnostics;

public sealed record DoctorLog(string? Path, string Text, bool HasFreshCrashReport);
public static class DoctorLogReader
{
    public static async Task<DoctorLog> ReadAsync(string game, DateTimeOffset since, CancellationToken token)
    {
        var logs = SafePaths.Resolve(game, "logs/latest.log");
        var crashDirectory = SafePaths.Resolve(game, "crash-reports");
        string? crash = null;
        if (Directory.Exists(crashDirectory))
        {
            SafePaths.NoLinks(crashDirectory);
            crash = Directory.EnumerateFiles(crashDirectory, "*.txt").Take(512)
                .Where(x => File.GetLastWriteTimeUtc(x) >= since.UtcDateTime && File.GetLastWriteTimeUtc(x) <= DateTime.UtcNow.AddMinutes(1))
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }
        var crashText = crash is null ? "" : await TailAsync(crash, token).ConfigureAwait(false);
        var latest = File.Exists(logs) && File.GetLastWriteTimeUtc(logs) >= since.UtcDateTime
            ? await TailAsync(logs, token).ConfigureAwait(false) : "";
        return new(crash ?? (latest.Length > 0 ? logs : null),
            (crashText + "\n" + latest).Trim(), crashText.Contains("---- Minecraft Crash Report ----", StringComparison.Ordinal));
    }
    public static async Task<string> TailAsync(string path, CancellationToken token)
    {
        SafePaths.NoLinks(path);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        if (file.Length > 120 * 1024) file.Seek(-120 * 1024, SeekOrigin.End);
        using var reader = new StreamReader(file, Encoding.UTF8, true, 4096, true);
        return LauncherLog.Sanitize(await reader.ReadToEndAsync(token).ConfigureAwait(false));
    }
}
