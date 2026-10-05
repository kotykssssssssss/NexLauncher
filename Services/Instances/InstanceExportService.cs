using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NexLauncher.Models;
using NexLauncher.Services.Modrinth;
using NexLauncher.Services.Network;
using NexLauncher.Services.Storage;

namespace NexLauncher.Services.Instances;

public sealed class InstanceExportService(ModManager mods)
{
    public Task<InstanceExportPlan> PlanAsync(GameInstance instance, string game, InstanceExportOptions options,
        IProgress<LaunchProgress> progress, CancellationToken token) => Task.Run(async () =>
    {
        SafePaths.NoLinks(game);
        var installed = await mods.LoadAsync(game, token).ConfigureAwait(false);
        var manifest = new InstanceManifest
        {
            Name = instance.Name, MinecraftVersion = instance.VersionId, Loader = instance.Loader, LoaderVersion = instance.LoaderVersion,
            MemoryMb = instance.MemoryMb, ExportedUtc = DateTimeOffset.UtcNow, LauncherVersion = BuildInfo.Version,
            OriginalModpackProjectId = instance.ModrinthProjectId, OriginalModpackVersionId = instance.ModrinthVersionId
        };
        var notices = new List<string>();
        var integrity = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var managedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in installed.Projects)
        {
            token.ThrowIfCancellationRequested();
            if (!ModrinthService.IsCompatible(mod.Version, instance))
                notices.Add("Проблема: metadata «" + mod.Title + "» не соответствует Minecraft/loader сборки. Import повторно проверит её через API.");
            var file = ModrinthService.PrimaryFile(mod.Version, ".jar");
            if (file.Filename != mod.FileName || !SameHashes(file.Hashes, mod.Hashes))
                throw new InvalidDataException("Metadata файла «" + mod.Title + "» противоречива. Экспорт остановлен; оригинал сохранён.");
            var relative = "mods/" + mod.FileName; managedPaths.Add(relative);
            integrity[relative] = await LauncherHttp.MatchesAsync(SafePaths.Resolve(game, relative), mod.Hashes, token).ConfigureAwait(false);
            if (!integrity[relative])
                notices.Add("Проблема: «" + mod.FileName + "» отсутствует/изменён. В export попадёт исходная Modrinth-версия, а не изменённый файл.");
            manifest.Mods.Add(new(mod.Version.ProjectId, mod.Version.Id, mod.FileName, new(mod.Hashes), file.Size, mod.Explicit));
            foreach (var dependency in mod.Version.Dependencies.Where(x => x.DependencyType is "required" or "incompatible"))
            {
                var matches = installed.Projects.Any(x => (dependency.ProjectId is null || dependency.ProjectId == x.Version.ProjectId) &&
                    (dependency.VersionId is null || dependency.VersionId == x.Version.Id));
                if (dependency.DependencyType == "required" && ((dependency.ProjectId is null && dependency.VersionId is null) || !matches))
                    notices.Add("Проблема: metadata не содержит exact required dependency для «" + mod.Title + "». Ручной JAR не подтверждает project ID; import может быть отклонён.");
                if (dependency.DependencyType == "incompatible" && (dependency.ProjectId is not null || dependency.VersionId is not null) && matches)
                    notices.Add("Проблема: metadata содержит incompatible dependency для «" + mod.Title + "». Import остановится до установки.");
            }
        }
        var files = Discover(game, token);
        for (var i = 0; i < files.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var relative = files[i]; if (managedPaths.Contains(relative)) continue;
            var selected = relative.Split('/')[0] switch
            {
                "mods" => options.LocalMods,
                "config" or "defaultconfigs" => options.Configs,
                "resourcepacks" => options.ResourcePacks,
                "shaderpacks" => options.ShaderPacks,
                _ => false
            };
            if (!selected || !InstanceManifestService.IsAllowedContent(relative)) { manifest.OmittedFiles.Add(relative); continue; }
            progress.Report(new($"Export preview: {i + 1}/{files.Count}", i * 100d / Math.Max(1, files.Count)));
            await using var input = File.OpenRead(SafePaths.Resolve(game, relative));
            if (input.Length > InstanceManifestService.MaxFileBytes) throw new InvalidDataException("Файл слишком велик: " + relative);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));
            manifest.Files.Add(new(relative, input.Length, hash));
        }
        if (manifest.OmittedFiles.Any(x => x.StartsWith("mods/", StringComparison.Ordinal)))
            notices.Add("Local/unknown файлы в mods не включены. Сборка после импорта может быть неполной.");
        if (options.Configs) notices.Add("Configs могут содержать адреса серверов, пароли и другие личные данные. Проверь список и содержимое перед передачей архива.");
        if (manifest.Files.Count > 0) notices.Add("Bundled-файлы передаются локально. Убедись, что лицензии/права автора допускают их передачу получателю.");
        if (!Directory.Exists(game)) notices.Add("Папка игры пока отсутствует; экспортируется рецепт установки, без пользовательских файлов.");
        InstanceManifestService.Validate(manifest);
        return new InstanceExportPlan(manifest, game, notices.Distinct().ToArray(), integrity);
    }, token);

    public Task ExportAsync(InstanceExportPlan plan, string destination, IProgress<LaunchProgress> progress, CancellationToken token) => Task.Run(async () =>
    {
        if (!Path.IsPathFullyQualified(destination) || !destination.EndsWith(".nexpack", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Выбери полный путь к файлу .nexpack.");
        SafePaths.NoLinks(destination); InstanceManifestService.Validate(plan.Manifest);
        var current = await mods.LoadAsync(plan.SourceDirectory, token).ConfigureAwait(false);
        if (current.Projects.Count != plan.Manifest.Mods.Count || current.Projects.Any(x => !plan.Manifest.Mods.Any(y =>
            y.ProjectId == x.Version.ProjectId && y.VersionId == x.Version.Id && y.Filename == x.FileName && y.Explicit == x.Explicit && SameHashes(y.Hashes, x.Hashes))))
            throw new IOException("Metadata модов изменилась после preview. Обнови preview и повтори export.");
        foreach (var mod in plan.Manifest.Mods)
        {
            var relative = "mods/" + mod.Filename;
            if (!plan.ManagedIntegrity.TryGetValue(relative, out var intact) ||
                intact != await LauncherHttp.MatchesAsync(SafePaths.Resolve(plan.SourceDirectory, relative), mod.Hashes, token).ConfigureAwait(false))
                throw new IOException("Состояние managed JAR изменилось после preview. Обнови preview перед export.");
        }
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    await using (var manifestStream = zip.CreateEntry(InstanceManifestService.EntryName).Open())
                        await JsonSerializer.SerializeAsync(manifestStream, plan.Manifest, InstanceManifestService.Json, token).ConfigureAwait(false);
                    for (var i = 0; i < plan.Manifest.Files.Count; i++)
                    {
                        var file = plan.Manifest.Files[i];
                        progress.Report(new($"Export: {i + 1}/{plan.Manifest.Files.Count} — {file.Path}", i * 100d / Math.Max(1, plan.Manifest.Files.Count)));
                        await using var input = File.OpenRead(SafePaths.Resolve(plan.SourceDirectory, file.Path));
                        await using var entry = zip.CreateEntry("files/" + file.Path, CompressionLevel.Fastest).Open();
                        await InstancePackageReader.CopyVerifiedAsync(input, entry, file.Size, file.Sha256, token).ConfigureAwait(false);
                    }
                }
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            // Validate the actual archive, not just its in-memory plan, before replacing a user's export.
            using (await new InstancePackageReader().OpenAsync(temporary, token).ConfigureAwait(false)) { }
            token.ThrowIfCancellationRequested(); SafePaths.NoLinks(destination);
            File.Move(temporary, destination, true);
            progress.Report(new("Export готов", 100));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }, token);

    internal static bool SameHashes(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(x => b.TryGetValue(x.Key, out var value) && value.Equals(x.Value, StringComparison.OrdinalIgnoreCase));
    private static List<string> Discover(string game, CancellationToken token)
    {
        var files = new List<string>(); var directories = new Stack<string>(); var count = 0;
        foreach (var root in new[] { "mods", "config", "defaultconfigs", "resourcepacks", "shaderpacks" })
        { var path = SafePaths.Resolve(game, root); if (Directory.Exists(path)) directories.Push(path); }
        while (directories.Count > 0)
            foreach (var entry in Directory.EnumerateFileSystemEntries(directories.Pop()))
            {
                token.ThrowIfCancellationRequested(); SafePaths.NoLinks(entry);
                if (++count > InstanceManifestService.MaxFiles) throw new InvalidDataException("Слишком много пользовательских файлов/папок для export (8192).");
                if (Directory.Exists(entry)) directories.Push(entry);
                else files.Add(Path.GetRelativePath(game, entry).Replace('\\', '/'));
            }
        files.Sort(StringComparer.Ordinal); return files;
    }
}
