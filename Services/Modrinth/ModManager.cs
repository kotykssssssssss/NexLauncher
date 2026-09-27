using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NexLauncher.Models;
using NexLauncher.Services.Network;
using NexLauncher.Services.Storage;

namespace NexLauncher.Services.Modrinth;

public sealed class ModManager(IModrinthService api, LauncherHttp http)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public static string MetadataPath(string game) => SafePaths.Resolve(game, ".nexlauncher/mods.json");
    private static string JournalPath(string game) => SafePaths.Resolve(game, ".nexlauncher/transaction.json");
    public static void EnsureReady(string game)
    {
        if (File.Exists(JournalPath(game))) throw new InvalidDataException("Незавершённая операция с модами. Исходные файлы и журнал сохранены в .nexlauncher; восстанови их по docs/MODRINTH.md перед запуском.");
    }
    public async Task<InstalledMods> LoadAsync(string game, CancellationToken token)
    {
        EnsureReady(game);
        var value = await AtomicJson.ReadAsync<InstalledMods>(MetadataPath(game), token).ConfigureAwait(false) ?? new();
        if (value.FormatVersion != 1 || value.Projects is null || value.Projects.Count > 256) throw new InvalidDataException("Неизвестный формат metadata модов. Оригинал сохранён.");
        var projects = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in value.Projects)
        {
            if (mod is null || mod.Version is null || mod.FileName is null || mod.Hashes is null || mod.Title is null)
                throw new InvalidDataException("Неполная metadata модов. Оригинал сохранён.");
            ModrinthService.ValidateVersion(mod.Version);
            if (!projects.Add(mod.Version.ProjectId) || !paths.Add(mod.FileName)) throw new InvalidDataException("Дубли в metadata модов. Оригинал сохранён.");
            _ = SafePaths.Resolve(game, "mods/" + mod.FileName);
            if (mod.FileName.Contains('/') || !mod.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Некорректное имя мода.");
            LauncherHttp.ValidateHashes(mod.Hashes);
        }
        return value;
    }
    public async Task<ModInstallPlan> PlanAsync(GameInstance instance, string game, string versionId, CancellationToken token) =>
        await new DependencyResolver(api).ResolveAsync(instance, versionId, await LoadAsync(game, token).ConfigureAwait(false), token).ConfigureAwait(false);

    public async Task InstallAsync(GameInstance instance, string game, string versionId, IProgress<LaunchProgress> progress, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var before = await LoadAsync(game, token).ConfigureAwait(false);
            var plan = await new DependencyResolver(api).ResolveAsync(instance, versionId, before, token).ConfigureAwait(false);
            var proposed = plan.Projects.ToDictionary(x => x.Version.ProjectId, StringComparer.Ordinal);
            foreach (var entry in proposed.Values)
                entry.Explicit |= before.Projects.Any(x => x.Version.ProjectId == entry.Version.ProjectId && x.Explicit);
            var after = new InstalledMods { Projects = before.Projects.Where(x => !proposed.ContainsKey(x.Version.ProjectId)).Concat(proposed.Values).ToList() };
            if (after.Projects.Select(x => x.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != after.Projects.Count)
                throw new InvalidOperationException("Имена файлов двух модов совпадают. Установка остановлена.");
            var changed = proposed.Values.Where(x => !before.Projects.Any(old => old.Version.Id == x.Version.Id && old.FileName == x.FileName)).ToArray();
            // Same metadata does not imply that the local JAR is intact.
            foreach (var unchanged in proposed.Values.Except(changed))
                if (!await LauncherHttp.MatchesAsync(SafePaths.Resolve(game, "mods/" + unchanged.FileName), unchanged.Hashes, token).ConfigureAwait(false))
                    throw new IOException("Файл «" + unchanged.FileName + "» изменён или отсутствует. Восстанови его или удали через список модов перед установкой.");
            var replaced = before.Projects.Where(x => changed.Any(next => next.Version.ProjectId == x.Version.ProjectId)).ToArray();
            await CommitAsync(game, before, after, changed, replaced, progress, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(string game, string projectId, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var before = await LoadAsync(game, token).ConfigureAwait(false);
            var target = before.Projects.SingleOrDefault(x => x.Version.ProjectId == projectId) ?? throw new InvalidOperationException("Мод уже удалён.");
            foreach (var other in before.Projects.Where(x => x != target))
                if (other.Version.Dependencies.Any(x => x.DependencyType == "required" &&
                    (x.ProjectId == projectId || x.VersionId == target.Version.Id)))
                    throw new InvalidOperationException("Этот мод нужен «" + other.Title + "». Сначала удали зависимый проект.");
            await CommitAsync(game, before, new() { Projects = before.Projects.Where(x => x != target).ToList() }, [], [target],
                new Progress<LaunchProgress>(), token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task CommitAsync(string game, InstalledMods before, InstalledMods after, IReadOnlyList<InstalledMod> additions,
        IReadOnlyList<InstalledMod> removals, IProgress<LaunchProgress> progress, CancellationToken token)
    {
        var stageRoot = SafePaths.Resolve(game, ".nexlauncher");
        var stage = SafePaths.Resolve(stageRoot, ".nex-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var backedUp = new List<(string Original, string Backup)>();
        var published = new List<string>();
        var journal = JournalPath(game);
        var journalWritten = false;
        var committed = false;
        try
        {
            foreach (var removal in removals)
            {
                var file = SafePaths.Resolve(game, "mods/" + removal.FileName);
                if (File.Exists(file) && !await LauncherHttp.MatchesAsync(file, removal.Hashes, token).ConfigureAwait(false))
                    throw new IOException("Файл «" + removal.FileName + "» изменён вручную. Launcher не будет его удалять или перезаписывать.");
            }
            foreach (var addition in additions)
            {
                var destination = SafePaths.Resolve(game, "mods/" + addition.FileName);
                if (File.Exists(destination) && !removals.Any(x => x.FileName.Equals(addition.FileName, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("Файл «" + addition.FileName + "» уже существует и не принадлежит этой операции. Ручные моды остаются без изменений.");
                var file = ModrinthService.PrimaryFile(addition.Version, ".jar");
                await http.DownloadAsync(file.Url, SafePaths.Resolve(stage, addition.FileName), file.Hashes, file.Size, ["cdn.modrinth.com"], progress, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            var current = await LoadAsync(game, token).ConfigureAwait(false);
            if (JsonSerializer.Serialize(current) != JsonSerializer.Serialize(before))
                throw new IOException("Metadata модов изменилась во время загрузки. Обнови список и повтори.");
            // Journal and backup are durable before any existing JAR changes. A crash blocks further writes/launch.
            await AtomicJson.WriteAsync(SafePaths.Resolve(stage, "before.json"), before, token).ConfigureAwait(false);
            await AtomicJson.WriteAsync(journal, new { FormatVersion = 1, Stage = stage, Remove = removals.Select(x => x.FileName), Add = additions.Select(x => x.FileName) }, token).ConfigureAwait(false);
            journalWritten = true;
            Directory.CreateDirectory(SafePaths.Resolve(game, "mods"));
            foreach (var removal in removals)
            {
                var file = SafePaths.Resolve(game, "mods/" + removal.FileName);
                var backup = SafePaths.Resolve(stage, "backup/" + removal.FileName);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                if (File.Exists(file))
                {
                    File.Move(file, backup); backedUp.Add((file, backup));
                    if (!await LauncherHttp.MatchesAsync(backup, removal.Hashes, CancellationToken.None).ConfigureAwait(false))
                        throw new IOException("Мод изменился во время загрузки. Исходный файл будет восстановлен.");
                }
            }
            foreach (var addition in additions)
            {
                var file = SafePaths.Resolve(game, "mods/" + addition.FileName);
                File.Move(SafePaths.Resolve(stage, addition.FileName), file, false); published.Add(file);
            }
            // Cancellation after the commit starts must not leave half an installation.
            await AtomicJson.WriteAsync(MetadataPath(game), after, CancellationToken.None).ConfigureAwait(false);
            committed = true;
            File.Delete(journal);
            journalWritten = false;
        }
        catch
        {
            if (!committed)
            {
                foreach (var file in published) { SafePaths.NoLinks(file); File.Delete(file); }
                foreach (var (original, backup) in backedUp) { SafePaths.NoLinks(original); File.Move(backup, original, false); }
                if (journalWritten) { File.Delete(journal); journalWritten = false; }
            }
            throw;
        }
        finally
        {
            if (!journalWritten) SafePaths.DeleteStaging(stageRoot, stage);
        }
    }
}
