using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NexLauncher.Models;

namespace NexLauncher.Services.Modrinth;

public sealed record ModInstallPlan(string RootProjectId, IReadOnlyList<InstalledMod> Projects, IReadOnlyList<string> Notices);

public sealed class DependencyResolver(IModrinthService api)
{
    public async Task<ModInstallPlan> ResolveAsync(GameInstance instance, string versionId, InstalledMods installed, CancellationToken token)
    {
        if (instance.Loader == ModLoader.Vanilla) throw new InvalidOperationException("Моды требуют Fabric, Forge или NeoForge.");
        var selected = new Dictionary<string, InstalledMod>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var notices = new List<string>();
        var root = await api.VersionAsync(versionId, token).ConfigureAwait(false);
        async Task Visit(ModrinthVersion version)
        {
            token.ThrowIfCancellationRequested();
            if (selected.Count >= 256 && !selected.ContainsKey(version.ProjectId)) throw new InvalidDataException("Слишком много зависимостей.");
            if (selected.TryGetValue(version.ProjectId, out var existing))
            {
                if (existing.Version.Id != version.Id) throw new InvalidOperationException("Зависимости требуют разные версии одного проекта: " + existing.Title);
                return; // A cycle is resolved once; no second download or recursive loop.
            }
            if (!visiting.Add(version.ProjectId)) return;
            var project = await api.ProjectAsync(version.ProjectId, token).ConfigureAwait(false);
            ModrinthService.ValidateCompatibility(version, project, instance);
            var file = ModrinthService.PrimaryFile(version, ".jar");
            selected.Add(version.ProjectId, new InstalledMod { Title = project.Title, Version = version, FileName = file.Filename, Hashes = file.Hashes, Explicit = version.ProjectId == root.ProjectId });
            foreach (var dependency in version.Dependencies)
            {
                if (dependency.DependencyType is "optional" or "embedded")
                {
                    notices.Add((dependency.DependencyType == "optional" ? "Необязательная зависимость (не устанавливается): " : "Встроенная зависимость (повторно не скачивается): ") + (dependency.ProjectId ?? dependency.VersionId ?? dependency.FileName));
                    continue;
                }
                if (dependency.DependencyType == "incompatible")
                {
                    if (dependency.ProjectId is null && dependency.VersionId is null)
                        throw new InvalidOperationException("Невозможно проверить конфликт без project/version ID: " + dependency.FileName);
                    continue;
                }
                if (dependency.DependencyType != "required") throw new InvalidDataException("Неизвестный тип зависимости Modrinth.");
                ModrinthVersion next;
                if (dependency.VersionId is not null) next = await api.VersionAsync(dependency.VersionId, token).ConfigureAwait(false);
                else if (dependency.ProjectId is not null)
                {
                    var chosen = selected.GetValueOrDefault(dependency.ProjectId)?.Version ?? installed.Projects.FirstOrDefault(x => x.Version.ProjectId == dependency.ProjectId)?.Version;
                    next = chosen is not null ? await api.VersionAsync(chosen.Id, token).ConfigureAwait(false) :
                        (await api.VersionsAsync(dependency.ProjectId, instance, token).ConfigureAwait(false)).FirstOrDefault()
                        ?? throw new InvalidOperationException("Нет совместимой required dependency: " + dependency.ProjectId);
                }
                else throw new InvalidOperationException("Required dependency не имеет project/version ID. Установи её вручную по инструкции автора.");
                if (dependency.ProjectId is not null && next.ProjectId != dependency.ProjectId) throw new InvalidDataException("Зависимость ссылается на неверный проект.");
                await Visit(next).ConfigureAwait(false);
            }
            visiting.Remove(version.ProjectId);
        }
        await Visit(root).ConfigureAwait(false);
        var combined = installed.Projects.Where(x => !selected.ContainsKey(x.Version.ProjectId)).Concat(selected.Values).ToArray();
        foreach (var mod in combined)
        {
            ModrinthService.ValidateVersion(mod.Version);
            foreach (var dep in mod.Version.Dependencies)
            {
                bool Matches(InstalledMod candidate) => (dep.ProjectId is null || dep.ProjectId == candidate.Version.ProjectId) && (dep.VersionId is null || dep.VersionId == candidate.Version.Id);
                if (dep.DependencyType == "incompatible" && (dep.ProjectId is not null || dep.VersionId is not null) && combined.Any(Matches))
                    throw new InvalidOperationException("Конфликт зависимости у «" + mod.Title + "». Удали несовместимый проект или выбери другую версию.");
                if (dep.DependencyType == "required" && !combined.Any(Matches))
                    throw new InvalidOperationException("Обновление нарушит required dependency проекта «" + mod.Title + "».");
            }
        }
        return new(root.ProjectId, selected.Values.ToArray(), notices.Distinct().ToArray());
    }
}
