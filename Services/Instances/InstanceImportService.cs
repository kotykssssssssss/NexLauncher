using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NexLauncher.Models;
using NexLauncher.Services.Modrinth;
using NexLauncher.Services.Network;
using NexLauncher.Services.Storage;

namespace NexLauncher.Services.Instances;

public sealed class InstanceImportService(IModrinthService api, LauncherHttp http, IMinecraftService minecraft)
{
    public Task<GameInstance> ImportAsync(string path, InstanceImportPreview preview, string name, string root,
        IReadOnlyCollection<string> existingNames, Func<GameInstance, CancellationToken, Task> publish,
        IProgress<LaunchProgress> progress, CancellationToken token) => Task.Run(async () =>
    {
        name = InstanceManifestService.Name(name);
        if (existingNames.Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Сборка с таким именем уже есть. Выбери другое имя; существующая сборка не будет перезаписана.");
        progress.Report(new("Проверка package и hashes…", null));
        using var package = await new InstancePackageReader().OpenAsync(path, token).ConfigureAwait(false);
        if (package.Sha256 != preview.PackageSha256) throw new InvalidDataException("Package изменился после preview. Выбери файл заново.");
        var instance = InstanceManifestService.ToInstance(package.Manifest, name);
        // All API metadata and dependencies are validated before the official installer starts.
        var installed = await ResolveExactAsync(instance, package.Manifest.Mods, token).ConfigureAwait(false);
        root = await SafePaths.CheckWritableRootAsync(root, token).ConfigureAwait(false);
        var stage = SafePaths.Resolve(root, ".nex-stage-" + Guid.NewGuid().ToString("N"));
        var staged = SafePaths.Resolve(stage, instance.Id);
        var final = SafePaths.Resolve(root, instance.Id);
        var moved = false;
        Directory.CreateDirectory(staged);
        try
        {
            instance.GameDirectory = SafePaths.Resolve(staged, "game");
            Directory.CreateDirectory(instance.GameDirectory);
            await minecraft.InstallAsync(instance, progress, token).ConfigureAwait(false);
            for (var i = 0; i < installed.Projects.Count; i++)
            {
                var mod = installed.Projects[i]; var file = ModrinthService.PrimaryFile(mod.Version, ".jar");
                progress.Report(new($"Import mods: {i + 1}/{installed.Projects.Count}", i * 100d / Math.Max(1, installed.Projects.Count)));
                await http.DownloadAsync(file.Url, SafePaths.Resolve(instance.GameDirectory, "mods/" + mod.FileName), file.Hashes,
                    file.Size, ["cdn.modrinth.com"], progress, token).ConfigureAwait(false);
            }
            for (var i = 0; i < package.Manifest.Files.Count; i++)
            {
                var file = package.Manifest.Files[i]; token.ThrowIfCancellationRequested();
                progress.Report(new($"Import files: {i + 1}/{package.Manifest.Files.Count}", i * 100d / Math.Max(1, package.Manifest.Files.Count)));
                var target = SafePaths.Resolve(instance.GameDirectory, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = package.Archive.GetEntry("files/" + file.Path)!.Open();
                // Never overwrite loader-generated files, managed JARs, or preexisting folders.
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
                await InstancePackageReader.CopyVerifiedAsync(input, output, file.Size, file.Sha256, token).ConfigureAwait(false);
            }
            if (installed.Projects.Count > 0) await AtomicJson.WriteAsync(ModManager.MetadataPath(instance.GameDirectory), installed, token).ConfigureAwait(false);
            foreach (var mod in installed.Projects)
                if (!await LauncherHttp.MatchesAsync(SafePaths.Resolve(instance.GameDirectory, "mods/" + mod.FileName), mod.Hashes, token).ConfigureAwait(false))
                    throw new InvalidDataException("Финальная проверка managed-файла не прошла.");
            if (!minecraft.IsInstalled(instance)) throw new IOException("Установка Minecraft/loader не подтверждена. Сборка не зарегистрирована.");
            await AtomicJson.WriteAsync(SafePaths.Resolve(instance.GameDirectory, ".nexlauncher/import.json"),
                new { FormatVersion = 1, package.Manifest.ManifestVersion, package.Sha256, package.Manifest.ExportedUtc,
                    ImportedUtc = DateTimeOffset.UtcNow, package.Manifest.OmittedFiles }, token).ConfigureAwait(false);
            await SafePaths.CheckTreeAsync(instance.GameDirectory, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(final) || File.Exists(final)) throw new IOException("Папка нового instance уже существует.");
            Directory.Move(staged, final); moved = true;
            instance.GameDirectory = SafePaths.Resolve(final, "game");
            try { await publish(instance, CancellationToken.None).ConfigureAwait(false); }
            catch { Directory.Move(final, staged); moved = false; throw; }
            progress.Report(new("Import завершён", 100));
            return instance;
        }
        finally
        {
            // A failure moving back must preserve the unpublished final directory for recovery.
            // Cleanup can only remove a verified owned staging tree, never another instance.
            if (!moved || !Directory.Exists(staged)) SafePaths.DeleteStaging(root, stage);
        }
    }, token);

    private async Task<InstalledMods> ResolveExactAsync(GameInstance instance, IReadOnlyList<PortableMod> references, CancellationToken token)
    {
        var result = new InstalledMods(); var projects = new Dictionary<string, ModrinthProject>(StringComparer.Ordinal);
        foreach (var reference in references)
        {
            var version = await api.VersionAsync(reference.VersionId, token).ConfigureAwait(false);
            if (version.Id != reference.VersionId || version.ProjectId != reference.ProjectId) throw new InvalidDataException("Modrinth вернул другой project/version ID.");
            var project = await api.ProjectAsync(reference.ProjectId, token).ConfigureAwait(false);
            ModrinthService.ValidateCompatibility(version, project, instance);
            var file = ModrinthService.PrimaryFile(version, ".jar");
            if (file.Filename != reference.Filename || file.Size != reference.Size ||
                reference.Hashes.Any(x => !file.Hashes.TryGetValue(x.Key, out var value) || !value.Equals(x.Value, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Exact Modrinth файл не совпадает с manifest: " + reference.Filename);
            projects[project.Id] = project;
            result.Projects.Add(new() { Title = project.Title, Version = version, FileName = file.Filename, Hashes = file.Hashes, Explicit = reference.Explicit });
        }
        // Reuse dependency/conflict semantics without substituting today's latest dependency.
        var resolver = new DependencyResolver(new ExactSnapshot(result, projects));
        foreach (var mod in result.Projects)
        {
            var plan = await resolver.ResolveAsync(instance, mod.Version.Id, result, token).ConfigureAwait(false);
            if (plan.Projects.Any(x => !result.Projects.Any(y => y.Version.Id == x.Version.Id)))
                throw new InvalidDataException("Manifest не содержит exact required dependency. Повтори экспорт после её установки.");
        }
        return result;
    }
    private sealed class ExactSnapshot(InstalledMods installed, Dictionary<string, ModrinthProject> projects) : IModrinthService
    {
        public Task<ModrinthVersion> VersionAsync(string id, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(installed.Projects.FirstOrDefault(x => x.Version.Id == id)?.Version ??
                throw new InvalidDataException("В manifest нет exact required dependency " + id + ". Последняя версия не подставляется."));
        }
        public Task<ModrinthProject> ProjectAsync(string id, CancellationToken token) => Task.FromResult(projects[id]);
        public Task<IReadOnlyList<ModrinthVersion>> VersionsAsync(string id, GameInstance? instance, CancellationToken token) =>
            throw new InvalidDataException("В manifest нет exact required dependency проекта " + id + ". Последняя версия не подставляется.");
        public Task<ModrinthSearchResult> SearchAsync(string query, bool packs, GameInstance? instance, int offset, CancellationToken token, ModrinthSearchOptions? options = null) => throw new NotSupportedException();
        public Task<ModrinthFilterCatalog> FilterCatalogAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<ModrinthTeamMember>> MembersAsync(string id, CancellationToken token) => throw new NotSupportedException();
    }
}
