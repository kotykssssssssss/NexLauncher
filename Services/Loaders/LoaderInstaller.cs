using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CmlLib.Core;
using NexLauncher.Models;
using NexLauncher.Services.Network;
using NexLauncher.Services.Storage;

namespace NexLauncher.Services.Loaders;

public interface ILoaderInstaller
{
    Task<string> InstallAsync(GameInstance instance, MinecraftLauncher launcher, IProgress<LaunchProgress> progress, CancellationToken token);
}

/// <summary>Loader installation is independent of Modrinth. Returns CmlLib's concrete launch profile ID.</summary>
public sealed class LoaderInstaller : ILoaderInstaller
{
    private readonly LauncherHttp _http;
    private readonly ILoaderCatalog _catalog;
    public LoaderInstaller(LauncherHttp http, ILoaderCatalog catalog) { _http = http; _catalog = catalog; }
    public async Task<string> InstallAsync(GameInstance instance, MinecraftLauncher launcher, IProgress<LaunchProgress> progress, CancellationToken token)
    {
        LoaderCatalog.ValidateInstance(instance);
        if (instance.Loader == ModLoader.Vanilla) return instance.VersionId;
        var versions = await _catalog.GetVersionsAsync(instance.Loader, instance.VersionId, token).ConfigureAwait(false);
        if (!versions.Any(x => x.Version == instance.LoaderVersion)) throw new InvalidOperationException("Эта версия загрузчика не поддерживает выбранный Minecraft.");
        var path = launcher.MinecraftPath.BasePath;
        SafePaths.NoLinks(path);
        progress.Report(new("Установка " + instance.Loader + " " + instance.LoaderVersion));
        string id;
        if (instance.Loader == ModLoader.Fabric)
        {
            var bytes = await _http.GetBytesAsync($"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(instance.VersionId)}/{Uri.EscapeDataString(instance.LoaderVersion)}/profile/json", LauncherHttp.LoaderHosts, token).ConfigureAwait(false);
            using var profile = JsonDocument.Parse(bytes);
            id = ValidateProfile(profile.RootElement, instance.VersionId);
            var target = SafePaths.Resolve(path, $"versions/{id}/{id}.json");
            await AtomicJson.WriteAsync(target, profile.RootElement, token).ConfigureAwait(false);
        }
        else id = await InstallOfficialJarAsync(instance, launcher, progress, token).ConfigureAwait(false);
        await launcher.GetAllVersionsAsync(token).ConfigureAwait(false);
        return id;
    }

    public static string ValidateProfile(JsonElement profile, string minecraft)
    {
        var id = profile.GetProperty("id").GetString()!;
        LoaderCatalog.ValidateIdentifier(id);
        if (id == minecraft || profile.GetProperty("inheritsFrom").GetString() != minecraft)
            throw new InvalidDataException("Профиль загрузчика ссылается на другую версию Minecraft.");
        return id;
    }

    private async Task<string> InstallOfficialJarAsync(GameInstance instance, MinecraftLauncher launcher, IProgress<LaunchProgress> progress, CancellationToken token)
    {
        var game = launcher.MinecraftPath.BasePath;
        if (game.Contains('!')) throw new InvalidOperationException("Официальный Java installer не поддерживает ! в пути. Выбери другую папку для новой Forge/NeoForge сборки.");
        var stage = SafePaths.Resolve(game, ".nex-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var artifact = instance.Loader == ModLoader.Forge ? instance.VersionId + "-" + instance.LoaderVersion : instance.LoaderVersion;
            var url = instance.Loader == ModLoader.Forge
                ? $"https://maven.minecraftforge.net/net/minecraftforge/forge/{artifact}/forge-{artifact}-installer.jar"
                : $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{artifact}/neoforge-{artifact}-installer.jar";
            var checksum = Encoding.UTF8.GetString(await _http.GetBytesAsync(url + ".sha1", LauncherHttp.LoaderHosts, token, 4096).ConfigureAwait(false)).Trim().Split(' ')[0];
            var jar = SafePaths.Resolve(stage, "installer.jar");
            await _http.DownloadAsync(url, jar, new Dictionary<string, string> { ["sha1"] = checksum }, -1, LauncherHttp.LoaderHosts, progress, token).ConfigureAwait(false);
            string id;
            // Inspect before executing only the verified official installer. Never execute a pack-provided JAR.
            using (var zip = ZipFile.OpenRead(jar))
            {
                foreach (var entry in zip.Entries)
                    if (!entry.FullName.EndsWith('/')) _ = SafePaths.Resolve(stage, entry.FullName);
                using var profile = ReadJsonEntry(zip, "install_profile.json");
                if (!profile.RootElement.TryGetProperty("spec", out var spec) || spec.GetInt32() != 1 ||
                    profile.RootElement.GetProperty("minecraft").GetString() != instance.VersionId)
                    throw new InvalidDataException("Этот формат Forge installer пока не поддерживается или Minecraft не совпадает.");
                using var version = ReadJsonEntry(zip, "version.json");
                id = ValidateProfile(version.RootElement, instance.VersionId);
            }
            // The official installer expects launcher_profiles.json inside its explicit target directory.
            var profiles = SafePaths.Resolve(game, "launcher_profiles.json");
            if (!File.Exists(profiles)) await AtomicJson.WriteAsync(profiles, new { profiles = new { } }, token).ConfigureAwait(false);
            await SafePaths.CheckTreeAsync(game, token).ConfigureAwait(false);
            var vanilla = await launcher.GetVersionAsync(instance.VersionId, token).ConfigureAwait(false);
            var java = string.IsNullOrWhiteSpace(instance.JavaPath) ? launcher.GetJavaPath(vanilla) : instance.JavaPath;
            if (string.IsNullOrEmpty(java) || !File.Exists(java)) throw new InvalidOperationException("Не найдена Java для installer. Укажи Java в настройках сборки.");
            var start = new ProcessStartInfo(java) { WorkingDirectory = stage, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-Djava.awt.headless=true");
            start.ArgumentList.Add("-jar"); start.ArgumentList.Add(jar);
            start.ArgumentList.Add("--installClient"); start.ArgumentList.Add(game);
            progress.Report(new("Официальный " + instance.Loader + " installer проверяет библиотеки и подготавливает клиент…"));
            using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить официальный installer.");
            var logPath = SafePaths.Resolve(stage, "installer-output.log");
            using var log = new StreamWriter(logPath, false, Encoding.UTF8);
            var gate = new SemaphoreSlim(1);
            async Task Drain(StreamReader reader)
            {
                while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    await gate.WaitAsync().ConfigureAwait(false);
                    try { await log.WriteLineAsync(LauncherLog.Sanitize(line)).ConfigureAwait(false); } finally { gate.Release(); }
                }
            }
            var stdout = Drain(process.StandardOutput); var stderr = Drain(process.StandardError);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(20));
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                if (!token.IsCancellationRequested) throw new IOException("Время установки загрузчика истекло. Журнал: " + logPath);
                throw;
            }
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("Официальный installer завершился с ошибкой. Проверь Java. Журнал: " + logPath);
            var installed = SafePaths.Resolve(game, $"versions/{id}/{id}.json");
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(installed, token).ConfigureAwait(false));
            if (ValidateProfile(document.RootElement, instance.VersionId) != id) throw new InvalidDataException("Installer создал неожиданный профиль.");
            return id;
        }
        finally
        {
            // Retain the small installer log for diagnostics, but never retain executable downloads.
            var jar = SafePaths.Resolve(stage, "installer.jar");
            if (File.Exists(jar)) File.Delete(jar);
        }
    }

    private static JsonDocument ReadJsonEntry(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidDataException("Installer не содержит " + name);
        if (entry.Length > 4 * 1024 * 1024) throw new InvalidDataException("Слишком большая metadata installer.");
        using var stream = entry.Open();
        return JsonDocument.Parse(stream);
    }
}
