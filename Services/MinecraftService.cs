using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.FileExtractors;
using CmlLib.Core.Installers;
using CmlLib.Core.ProcessBuilder;
using CmlLib.Core.VersionLoader;
using NexLauncher.Models;
using NexLauncher.Services.Loaders;
using NexLauncher.Services.Network;
using NexLauncher.Services.Storage;

namespace NexLauncher.Services;

/// <summary>Repairs Minecraft files and installs a selected loader before the existing session/process pipeline.</summary>
public sealed class MinecraftService : IMinecraftService
{
    private const string MarkerName = ".nexlauncher-installed";
    private readonly string dataDirectory;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly ILoaderInstaller loaderInstaller;

    public MinecraftService(string dataDirectory, ILoaderInstaller? loaderInstaller = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        this.dataDirectory = Path.GetFullPath(dataDirectory);
        this.loaderInstaller = loaderInstaller ?? new LoaderInstaller(LauncherHttp.Shared, new LoaderCatalog(LauncherHttp.Shared));
    }

    public async Task<IReadOnlyList<MinecraftRelease>> GetVersionsAsync(CancellationToken cancellationToken)
    {
        // This directory only caches the official catalogue. It is not an instance.
        var launcher = CreateLauncher(Path.Combine(dataDirectory, "catalogue"));
        var versions = await launcher.GetAllVersionsAsync(cancellationToken).ConfigureAwait(false);
        return versions
            .Where(version => version.Type is "release" or "snapshot")
            .OrderByDescending(version => version.ReleaseTime)
            .Select(version => new MinecraftRelease(version.Name, version.Type!, version.ReleaseTime))
            .ToArray();
    }

    public bool IsInstalled(GameInstance instance)
    {
        try
        {
            var path = GetGamePath(instance);
            ValidateVersionId(instance.VersionId);
            var marker = Path.Combine(path, MarkerName);
            LoaderCatalog.ValidateInstance(instance);
            if (!File.Exists(marker)) return false;
            var fingerprint = File.ReadAllText(marker);
            var launchId = instance.VersionId;
            if (instance.Loader != ModLoader.Vanilla)
            {
                var parts = fingerprint.Split('|');
                if (parts.Length != 4 || parts[0] != instance.VersionId || parts[1] != instance.Loader.ToString() || parts[2] != instance.LoaderVersion) return false;
                launchId = parts[3];
                LoaderCatalog.ValidateIdentifier(launchId);
            }
            var versionDirectory = SafePaths.Resolve(path, "versions/" + launchId);
            return File.Exists(marker)
                && (instance.Loader != ModLoader.Vanilla || fingerprint == instance.VersionId)
                && IsNonEmptyFile(Path.Combine(versionDirectory, launchId + ".json"))
                && IsNonEmptyFile(SafePaths.Resolve(path, $"versions/{instance.VersionId}/{instance.VersionId}.jar"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public async Task InstallAsync(GameInstance instance, IProgress<LaunchProgress> progress,
        CancellationToken cancellationToken)
    {
        var settings = SnapshotAndValidate(instance);
        NexLauncher.Services.Modrinth.ModManager.EnsureReady(GetGamePath(settings));
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var launcher = CreateLauncher(GetGamePath(settings), settings.JavaPath);
            await InstallCoreAsync(launcher, settings, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<int> LaunchAsync(GameInstance instance, MSession session,
        IProgress<LaunchProgress> progress, Action<string> log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(log);
        cancellationToken.ThrowIfCancellationRequested();
        var settings = SnapshotAndValidate(instance);
        var launchSession = LaunchSessionPolicy.CopyForLaunch(session);
        NexLauncher.Services.Modrinth.ModManager.EnsureReady(GetGamePath(settings));
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var launcher = CreateLauncher(GetGamePath(settings), settings.JavaPath);
            // Verify hashes and recover missing files on every launch, even when a marker exists.
            var profile = await InstallCoreAsync(launcher, settings, progress, cancellationToken).ConfigureAwait(false);
            progress.Report(new LaunchProgress("Подготовка запуска…"));
            using var process = await launcher.BuildProcessAsync(profile.VersionId, new MLaunchOption
            {
                Session = launchSession,
                MaximumRamMb = settings.MemoryMb,
                JavaPath = profile.JavaPath,
                GameLauncherName = "NexLauncher",
                GameLauncherVersion = BuildInfo.Version
            }, cancellationToken).ConfigureAwait(false);

            if (!File.Exists(process.StartInfo.FileName))
                throw new InvalidOperationException("Подходящая Java не найдена. Укажите путь к Java в настройках сборки.");

            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start())
                throw new InvalidOperationException("Не удалось запустить Minecraft.");

            progress.Report(new LaunchProgress("Игра запущена", null, true));
            // Once started the game owns its lifetime. Cancellation/closing the launcher must not kill it.
            // Drain both pipes concurrently so a full stderr buffer cannot block the Java process.
            var logGate = new object();
            var stdout = ReadLogAsync(process.StandardOutput, log, logGate, launchSession);
            var stderr = ReadLogAsync(process.StandardError, log, logGate, launchSession);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return process.ExitCode;
        }
        finally
        {
            operationGate.Release();
        }
    }

    private static MinecraftLauncher CreateLauncher(string path, string? javaPath = null)
    {
        var parameters = MinecraftLauncherParameters.CreateDefault(new MinecraftPath(path));
        if (parameters.VersionLoader is MojangJsonVersionLoaderV2 loader)
            loader.UseLocalManifestWhenError = true;

        // Respect an explicitly selected Java instead of also downloading another runtime.
        if (!string.IsNullOrWhiteSpace(javaPath))
        {
            foreach (var extractor in parameters.FileExtractors!.OfType<JavaFileExtractor>().ToArray())
                parameters.FileExtractors!.Remove(extractor);
        }
        return new MinecraftLauncher(parameters);
    }

    private sealed record InstalledProfile(string VersionId, string? JavaPath);

    private async Task<InstalledProfile> InstallCoreAsync(MinecraftLauncher launcher, GameInstance instance,
        IProgress<LaunchProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        cancellationToken.ThrowIfCancellationRequested();
        var versionId = instance.VersionId;
        progress.Report(new LaunchProgress("Проверка версии и файлов…"));
        await SafePaths.CheckTreeAsync(launcher.MinecraftPath.BasePath, cancellationToken).ConfigureAwait(false);
        var versions = await launcher.GetAllVersionsAsync(cancellationToken).ConfigureAwait(false);
        if (!versions.Any(version => version.Name == versionId && version.Type is "release" or "snapshot"))
            throw new InvalidOperationException("Этой версии нет в официальном списке Minecraft. Обновите список версий.");

        var marker = Path.Combine(launcher.MinecraftPath.BasePath, MarkerName);
        // A failed repair must never leave an old success marker behind.
        File.Delete(marker);
        var reporter = new InstallProgressReporter(progress);
        await launcher.InstallAsync(versionId,
            new SyncProgress<InstallerProgressChangedEventArgs>(reporter.ReportFile),
            new SyncProgress<ByteProgress>(reporter.ReportBytes),
            cancellationToken).ConfigureAwait(false);
        var launchVersion = await loaderInstaller.InstallAsync(instance, launcher, progress, cancellationToken).ConfigureAwait(false);
        var vanilla = await launcher.GetVersionAsync(versionId, cancellationToken).ConfigureAwait(false);
        var java = string.IsNullOrWhiteSpace(instance.JavaPath) ? launcher.GetJavaPath(vanilla) : instance.JavaPath;
        if (instance.Loader != ModLoader.Vanilla)
        {
            // CmlLib extracts each inherited profile separately; an absent Java field on a loader
            // may otherwise download an unnecessary legacy runtime. The base game defines Java.
            foreach (var extractor in launcher.FileExtractors.OfType<JavaFileExtractor>().ToArray())
                launcher.FileExtractors.Remove(extractor);
            await launcher.InstallAsync(launchVersion,
                new SyncProgress<InstallerProgressChangedEventArgs>(reporter.ReportFile),
                new SyncProgress<ByteProgress>(reporter.ReportBytes), cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(launcher.MinecraftPath.BasePath);
        var temporaryMarker = marker + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var fingerprint = instance.Loader == ModLoader.Vanilla ? versionId : $"{versionId}|{instance.Loader}|{instance.LoaderVersion}|{launchVersion}";
            await File.WriteAllTextAsync(temporaryMarker, fingerprint, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryMarker, marker, true);
        }
        finally
        {
            try { File.Delete(temporaryMarker); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        progress.Report(new LaunchProgress("Minecraft установлен", 100));
        return new(launchVersion, java);
    }

    private string GetGamePath(GameInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (!Guid.TryParse(instance.Id, out var instanceId))
            throw new ArgumentException("Некорректный идентификатор сборки.", nameof(instance));
        return SafePaths.GamePath(dataDirectory, instance);
    }

    private static GameInstance SnapshotAndValidate(GameInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        LoaderCatalog.ValidateInstance(instance);
        if (!Guid.TryParse(instance.Id, out var instanceId))
            throw new ArgumentException("Некорректный идентификатор сборки.", nameof(instance));
        ValidateVersionId(instance.VersionId);
        if (instance.MemoryMb is < 1024 or > 32768)
            throw new ArgumentOutOfRangeException(nameof(instance), "Память должна быть от 1024 до 32768 МБ.");
        var javaPath = instance.JavaPath?.Trim() ?? "";
        if (javaPath.Length > 0 && (!Path.IsPathFullyQualified(javaPath) || !File.Exists(javaPath)))
            throw new ArgumentException("Укажите полный путь к существующему исполняемому файлу Java.", nameof(instance));
        return new GameInstance
        {
            Id = instanceId.ToString("N"),
            Name = instance.Name,
            VersionId = instance.VersionId,
            Loader = instance.Loader,
            LoaderVersion = instance.LoaderVersion,
            GameDirectory = instance.GameDirectory,
            MemoryMb = instance.MemoryMb,
            JavaPath = javaPath
        };
    }

    private static void ValidateVersionId(string versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId) || versionId.Length > 128 || versionId is "." or ".."
            || versionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || versionId.Contains('/') || versionId.Contains('\\')
            || versionId.EndsWith('.') || versionId != versionId.Trim())
            throw new ArgumentException("Выберите корректную версию Minecraft.", nameof(versionId));
    }

    private static bool IsNonEmptyFile(string path) => File.Exists(path) && new FileInfo(path).Length > 0;

    private static async Task ReadLogAsync(StreamReader reader, Action<string> log, object logGate, MSession session)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            var safeLine = GameLogRedactor.Redact(line, session.AccessToken, session.ClientToken);
            lock (logGate)
            {
                // A closed UI/log view must not stop draining the child process's output.
                try { log(safeLine); }
                catch (Exception) { }
            }
        }
    }

    private sealed class InstallProgressReporter(IProgress<LaunchProgress> target)
    {
        private readonly object gate = new();
        private long lastReport;
        private string fileName = "файлы игры";
        private double? percent;

        public void ReportFile(InstallerProgressChangedEventArgs value)
        {
            lock (gate)
            {
                if (!string.IsNullOrWhiteSpace(value.Name))
                    fileName = Path.GetFileName(value.Name);
                Report();
            }
        }

        public void ReportBytes(ByteProgress value)
        {
            lock (gate)
            {
                percent = value.TotalBytes > 0
                    ? Math.Clamp(value.ProgressedBytes * 100d / value.TotalBytes, 0, 100)
                    : null;
                Report();
            }
        }

        private void Report()
        {
            var now = Stopwatch.GetTimestamp();
            if (lastReport != 0 && Stopwatch.GetElapsedTime(lastReport, now).TotalMilliseconds < 150)
                return;
            lastReport = now;
            target.Report(new LaunchProgress($"Проверка и загрузка: {fileName}", percent));
        }
    }
}
