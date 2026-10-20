using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NexLauncher.Models;
using NexLauncher.Services.Modrinth;
using NexLauncher.Services.Network;
using NexLauncher.Services.Storage;

namespace NexLauncher.Services.Diagnostics;

public sealed class InstanceHealthService(ModManager mods, IMinecraftService minecraft)
{
    public Task<List<DiagnosticIssue>> ScanAsync(GameInstance instance, string game, CancellationToken token) => Task.Run(async () =>
    {
        var issues = new List<DiagnosticIssue>();
        void Add(string code, string title, string description, string evidence, DiagnosticSeverity severity = DiagnosticSeverity.Error,
            DiagnosticFix fix = DiagnosticFix.OpenMods, string? project = null) => issues.Add(new(code, severity, DiagnosticConfidence.Confirmed,
                title, description, evidence, "Проверь указанные данные. Безопасное исправление доступно только для управляемых файлов.", fix, project));
        if (!Directory.Exists(game))
        { Add("missing-game", "Игра ещё не установлена", "Папка сборки отсутствует.", "Game directory not found", fix: DiagnosticFix.RepairGame); return issues; }
        SafePaths.NoLinks(game);
        if (!minecraft.IsInstalled(instance)) Add("missing-game", "Файлы игры требуют проверки", "Отсутствует установленный профиль, marker или важный файл игры/загрузчика.", instance.Details, fix: DiagnosticFix.RepairGame);
        int? requiredJava = null;
        var versionJson = SafePaths.Resolve(game, $"versions/{instance.VersionId}/{instance.VersionId}.json");
        if (File.Exists(versionJson))
        {
            try
            {
                if (new FileInfo(versionJson).Length > 8 * 1024 * 1024) throw new InvalidDataException("Version JSON too large");
                using var version = JsonDocument.Parse(await File.ReadAllTextAsync(versionJson, token).ConfigureAwait(false));
                if (version.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid Minecraft profile structure");
                if (version.RootElement.TryGetProperty("javaVersion", out var java))
                {
                    if (java.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid javaVersion metadata");
                    if (java.TryGetProperty("majorVersion", out var required))
                    {
                        if (required.ValueKind != JsonValueKind.Number || !required.TryGetInt32(out var target) || target <= 0)
                            throw new InvalidDataException("Invalid Java major version");
                        requiredJava = target;
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
            { Add("game-profile", "Профиль Minecraft повреждён или недоступен", "Не удалось прочитать установленный version JSON.", LauncherLog.Sanitize(ex.Message), fix: DiagnosticFix.RepairGame); }
        }
        InstalledMods metadata;
        try { metadata = await mods.LoadAsync(game, token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { Add("mod-metadata", "Metadata модов недоступна", "Сканирование зависимостей пропущено; оригинал metadata сохранён.", LauncherLog.Sanitize(ex.Message), fix: DiagnosticFix.OpenFolder); metadata = new(); }
        foreach (var mod in metadata.Projects)
        {
            token.ThrowIfCancellationRequested();
            var v = mod.Version;
            if (!v.GameVersions.Contains(instance.VersionId)) Add("minecraft-mismatch", "Другая версия Minecraft", mod.Title + " не заявляет поддержку выбранной версии.", string.Join(", ", v.GameVersions));
            if (!v.Loaders.Contains(instance.Loader.ToString().ToLowerInvariant())) Add("loader-mismatch", "Несовместимый загрузчик", mod.Title + " не заявляет поддержку выбранного loader.", string.Join(", ", v.Loaders));
            var file = SafePaths.Resolve(game, "mods/" + mod.FileName);
            if (!File.Exists(file)) Add("missing-managed", "Отсутствует управляемый мод", mod.Title + " отсутствует на диске.", mod.FileName,
                fix: ModrinthService.IsCompatible(v, instance) ? DiagnosticFix.RestoreMissingManagedFile : DiagnosticFix.OpenMods, project: v.ProjectId);
            else if (!await LauncherHttp.MatchesAsync(file, mod.Hashes, token).ConfigureAwait(false))
                Add("managed-hash", "Управляемый файл изменён", "Hash не совпадает: " + mod.Title + ". Это может быть ручная замена; автоматическая перезапись запрещена.", mod.FileName, fix: DiagnosticFix.OpenFolder);
            foreach (var dep in v.Dependencies)
            {
                var matches = metadata.Projects.Where(x => dep.VersionId is { Length: > 0 } ? x.Version.Id == dep.VersionId : dep.ProjectId is { Length: > 0 } && x.Version.ProjectId == dep.ProjectId).ToArray();
                if (dep.DependencyType == "required" && (dep.VersionId is { Length: > 0 } || dep.ProjectId is { Length: > 0 }) && matches.Length == 0)
                    // A manual JAR may satisfy the dependency: absence from managed metadata is not proof of absence from the game.
                    issues.Add(new("missing-dependency", DiagnosticSeverity.Warning, DiagnosticConfidence.Possible, "Зависимость не учтена",
                        mod.Title + " требует dependency, которой нет в управляемой metadata. Ручной мод может удовлетворять требование.", dep.ProjectId ?? dep.VersionId ?? "",
                        "Проверь ручные моды и реальную ошибку загрузчика перед установкой.", DiagnosticFix.OpenMods));
                if (dep.DependencyType == "incompatible" && matches.Length > 0) Add("incompatible", "Конфликт managed-модов", mod.Title + " объявляет установленную dependency несовместимой.", dep.ProjectId ?? dep.VersionId ?? "");
            }
        }
        var folder = SafePaths.Resolve(game, "mods");
        var modIds = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Directory.Exists(folder))
        {
            SafePaths.NoLinks(folder); var count = 0;
            foreach (var file in Directory.EnumerateFiles(folder, "*.jar"))
            {
                token.ThrowIfCancellationRequested();
                if (++count > 1024) { issues.Add(new("scan-limit", DiagnosticSeverity.Warning, DiagnosticConfidence.Confirmed, "Лимит сканирования", "Проверены первые 1024 JAR.", "1024 files", "Проверь оставшиеся файлы вручную.")); break; }
                try
                {
                    SafePaths.NoLinks(file);
                    using var archive = ZipFile.OpenRead(file);
                    if (archive.Entries.Count == 0) throw new InvalidDataException("Пустой архив");
                    void CheckId(string key)
                    {
                        if (modIds.TryGetValue(key, out var previous) && previous != Path.GetFileName(file))
                            Add("duplicate-mod", "Два JAR с одинаковым mod ID", "Не удаляй файлы без проверки версии и backup.", key + ": " + previous + ", " + Path.GetFileName(file), DiagnosticSeverity.Warning, DiagnosticFix.OpenFolder);
                        else modIds[key] = Path.GetFileName(file);
                    }
                    var descriptor = archive.GetEntry("fabric.mod.json");
                    if (descriptor is not null)
                    {
                        if (descriptor.Length > 1024 * 1024) throw new InvalidDataException("Descriptor too large");
                        using var stream = descriptor.Open(); using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
                        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid Fabric descriptor structure");
                        if (json.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } key)
                        {
                            CheckId(key);
                        }
                        if (instance.Loader is ModLoader.Forge or ModLoader.NeoForge && archive.GetEntry("META-INF/mods.toml") is null && archive.GetEntry("META-INF/neoforge.mods.toml") is null)
                            issues.Add(new("local-loader", DiagnosticSeverity.Warning, DiagnosticConfidence.Likely, "Возможный mismatch ручного мода", Path.GetFileName(file) + " содержит Fabric descriptor без Forge/NeoForge descriptor.", "fabric.mod.json", "Проверь, не является ли JAR общей библиотекой или multi-loader модом.", DiagnosticFix.OpenFolder));
                    }
                    foreach (var name in new[] { "META-INF/mods.toml", "META-INF/neoforge.mods.toml" })
                    {
                        if (archive.GetEntry(name) is not { } toml) continue;
                        if (toml.Length > 1024 * 1024) throw new InvalidDataException("Descriptor too large");
                        using var stream = toml.Open(); using var reader = new StreamReader(stream);
                        var text = await reader.ReadToEndAsync(token).ConfigureAwait(false);
                        foreach (var id in ModDescriptors.TomlIds(text)) CheckId(id);
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException)
                { Add("invalid-jar", "Некорректный JAR/descriptor", Path.GetFileName(file) + " не удалось прочитать. Файл сохранён.", LauncherLog.Sanitize(ex.Message), fix: DiagnosticFix.OpenFolder); }
            }
        }
        if (instance.JavaPath.Length > 0)
        {
            if (!File.Exists(instance.JavaPath)) Add("java-missing", "Java недоступна", "Выбранный runtime не существует.", "Configured Java missing", fix: DiagnosticFix.AutomaticJava);
            else
            {
                var home = Path.GetDirectoryName(Path.GetDirectoryName(instance.JavaPath));
                var release = home is null ? null : Path.Combine(home, "release");
                try
                {
                    if (release is not null) SafePaths.NoLinks(release);
                    if (release is not null && File.Exists(release) && new FileInfo(release).Length < 65536)
                    {
                        var javaVersion = (await File.ReadAllLinesAsync(release, token).ConfigureAwait(false)).FirstOrDefault(x => x.StartsWith("JAVA_VERSION=", StringComparison.Ordinal));
                        var number = javaVersion?.Split('=')[1].Trim('"').Split('.')[0];
                        if (number == "1") number = javaVersion?.Split('=')[1].Trim('"').Split('.').ElementAtOrDefault(1);
                        if (int.TryParse(number, out var major) && requiredJava is { } target && major < target)
                            Add("java-mismatch", "Java ниже требования Minecraft", $"Выбрана Java {major}, профиль Minecraft требует Java {target}.", "JAVA_VERSION + javaVersion.majorVersion", fix: DiagnosticFix.AutomaticJava);
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                { issues.Add(new("java-metadata", DiagnosticSeverity.Info, DiagnosticConfidence.Unknown, "Версия Java не определена", "Runtime существует, но его release metadata недоступна.", LauncherLog.Sanitize(ex.Message), "Проверь выбранную Java вручную.")); }
            }
        }
        return issues;
    }, token);
}
