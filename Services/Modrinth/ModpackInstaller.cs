using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NexLauncher.Models;
using NexLauncher.Services.Network;
using NexLauncher.Services.Storage;

namespace NexLauncher.Services.Modrinth;

public sealed class ModpackInstaller(IModrinthService api, LauncherHttp http, IMinecraftService minecraft)
{
    public async Task<GameInstance> InstallAsync(string projectId, string versionId, string name, string root, bool includeOptional,
        Func<GameInstance, CancellationToken, Task> publish, IProgress<LaunchProgress> progress, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 60) throw new InvalidOperationException("Название сборки: 1–60 символов.");
        root = await SafePaths.CheckWritableRootAsync(root, token).ConfigureAwait(false);
        var project = await api.ProjectAsync(projectId, token).ConfigureAwait(false);
        var version = await api.VersionAsync(versionId, token).ConfigureAwait(false);
        if (project.ProjectType != "modpack" || version.ProjectId != projectId || (version.Environment is not null ? !ModrinthService.SupportsClient(version.Environment) : project.ClientSide is not ("required" or "optional")))
            throw new InvalidOperationException("Эта версия не является клиентским modpack.");
        var file = ModrinthService.PrimaryFile(version, ".mrpack");
        var stage = SafePaths.Resolve(root, ".nex-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        string? finalDirectory = null;
        string? stagedDirectory = null;
        try
        {
            var archive = SafePaths.Resolve(stage, "pack.mrpack");
            await http.DownloadAsync(file.Url, archive, file.Hashes, file.Size, ["cdn.modrinth.com"], progress, token).ConfigureAwait(false);
            using var zip = ZipFile.OpenRead(archive);
            var plan = await Task.Run(() => MrPackPlanner.Read(zip, stage), token).ConfigureAwait(false);
            var instance = plan.Instance;
            if (!version.GameVersions.Contains(instance.VersionId) || (instance.Loader != ModLoader.Vanilla && !version.Loaders.Contains(instance.Loader.ToString().ToLowerInvariant())))
                throw new InvalidDataException("Metadata Modrinth и manifest modpack не совпадают.");
            instance.Name = name.Trim(); instance.ModrinthProjectId = projectId; instance.ModrinthVersionId = versionId;
            stagedDirectory = SafePaths.Resolve(stage, instance.Id);
            var game = SafePaths.Resolve(stagedDirectory, "game");
            Directory.CreateDirectory(game);
            instance.GameDirectory = game;
            var files = plan.Files.Concat(includeOptional ? plan.OptionalFiles : []).ToArray();
            for (var i = 0; i < files.Length; i++)
            {
                var item = files[i];
                progress.Report(new($"Modpack: файл {i + 1}/{files.Length}", i * 100d / Math.Max(1, files.Length)));
                var destination = MrPackPlanner.ValidateContentPath(game, item.Path);
                Exception? last = null;
                foreach (var url in item.Downloads)
                {
                    try { await http.DownloadAsync(url, destination, item.Hashes, item.FileSize, LauncherHttp.PackHosts, progress, token).ConfigureAwait(false); last = null; break; }
                    catch (IOException ex) { last = ex; }
                }
                if (last is not null) throw last;
            }
            foreach (var entry in plan.Overrides)
            {
                token.ThrowIfCancellationRequested();
                var destination = MrPackPlanner.ValidateContentPath(game, entry.Destination);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                // Overwrite only our unpublished staging content; client-overrides was resolved in the plan.
                await using var input = zip.GetEntry(entry.Entry)!.Open();
                await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
                await input.CopyToAsync(output, token).ConfigureAwait(false);
            }
            await AtomicJson.WriteAsync(SafePaths.Resolve(game, ".nexlauncher/pack.json"), new { FormatVersion = 1, ProjectId = projectId, VersionId = versionId, IncludedOptional = includeOptional, Files = files.Select(x => x.Path), Overrides = plan.Overrides.Select(x => x.Destination) }, token).ConfigureAwait(false);
            await minecraft.InstallAsync(instance, progress, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            finalDirectory = SafePaths.Resolve(root, instance.Id);
            if (Directory.Exists(finalDirectory) || File.Exists(finalDirectory)) throw new IOException("Папка новой сборки уже существует.");
            Directory.Move(stagedDirectory, finalDirectory);
            instance.GameDirectory = SafePaths.Resolve(finalDirectory, "game");
            try { await publish(instance, CancellationToken.None).ConfigureAwait(false); }
            catch { Directory.Move(finalDirectory, stagedDirectory); finalDirectory = null; throw; }
            return instance;
        }
        finally { SafePaths.DeleteStaging(root, stage); }
    }
}
