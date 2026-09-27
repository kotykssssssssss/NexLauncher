using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NexLauncher.Models;
using NexLauncher.Services.Network;

namespace NexLauncher.Services.Modrinth;

public interface IModrinthService
{
    Task<ModrinthSearchResult> SearchAsync(string query, bool packs, GameInstance? instance, int offset, CancellationToken token);
    Task<ModrinthProject> ProjectAsync(string id, CancellationToken token);
    Task<IReadOnlyList<ModrinthTeamMember>> MembersAsync(string projectId, CancellationToken token);
    Task<IReadOnlyList<ModrinthVersion>> VersionsAsync(string projectId, GameInstance? instance, CancellationToken token);
    Task<ModrinthVersion> VersionAsync(string id, CancellationToken token);
}

public sealed class ModrinthService(LauncherHttp http) : IModrinthService
{
    public const string Api = "https://api.modrinth.com/v2/";
    private static readonly string[] Hosts = ["api.modrinth.com"];
    public static string SearchPath(string query, bool packs, GameInstance? instance, int offset)
    {
        if (query.Length > 200 || offset < 0) throw new ArgumentException("Некорректный запрос поиска.");
        var facets = new List<string[]> { new[] { "project_type:" + (packs ? "modpack" : "mod") } };
        if (!packs)
        {
            if (instance is null || instance.Loader == ModLoader.Vanilla) throw new InvalidOperationException("Для модов создай сборку Fabric, Forge или NeoForge.");
            facets.Add(["versions:" + instance.VersionId]);
            facets.Add(["categories:" + instance.Loader.ToString().ToLowerInvariant()]);
        }
        return "search?query=" + Uri.EscapeDataString(query) + "&facets=" + Uri.EscapeDataString(JsonSerializer.Serialize(facets)) + "&limit=20&offset=" + offset;
    }
    public async Task<ModrinthSearchResult> SearchAsync(string query, bool packs, GameInstance? instance, int offset, CancellationToken token)
    {
        var result = await http.JsonAsync<ModrinthSearchResult>(Api + SearchPath(query, packs, instance, offset), Hosts, token).ConfigureAwait(false);
        if (result.Hits is null || result.Hits.Count > 100 || result.TotalHits < 0) throw new InvalidDataException("Неожиданный ответ поиска Modrinth.");
        foreach (var hit in result.Hits)
        {
            if (hit is null || hit.Title is null || hit.Description is null || hit.Author is null || hit.Versions is null || hit.Categories is null)
                throw new InvalidDataException("Неполный результат поиска Modrinth.");
            ValidateId(hit.ProjectId);
        }
        return result;
    }
    public async Task<ModrinthProject> ProjectAsync(string id, CancellationToken token)
    {
        ValidateId(id);
        var project = await http.JsonAsync<ModrinthProject>(Api + "project/" + id, Hosts, token).ConfigureAwait(false);
        if (project.Id != id || project.ProjectType is not ("mod" or "modpack") || project.Title is null || project.Description is null || project.Body is null ||
            project.GameVersions is null || project.Loaders is null || project.Downloads < 0)
            throw new InvalidDataException("Неподдерживаемый тип или неполная metadata проекта Modrinth.");
        return project;
    }
    public async Task<IReadOnlyList<ModrinthTeamMember>> MembersAsync(string projectId, CancellationToken token)
    {
        ValidateId(projectId);
        var members = await http.JsonAsync<List<ModrinthTeamMember>>(Api + "project/" + projectId + "/members", Hosts, token).ConfigureAwait(false);
        if (members.Count > 256 || members.Any(x => x?.User?.Username is null || x.Role is null))
            throw new InvalidDataException("Неполные сведения об авторах Modrinth.");
        return members.OrderBy(x => x.Ordering).ToArray();
    }
    public async Task<IReadOnlyList<ModrinthVersion>> VersionsAsync(string projectId, GameInstance? instance, CancellationToken token)
    {
        ValidateId(projectId);
        var url = Api + "project/" + projectId + "/version";
        if (instance is not null)
            url += "?game_versions=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { instance.VersionId })) + "&loaders=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { instance.Loader.ToString().ToLowerInvariant() }));
        var versions = await http.JsonAsync<List<ModrinthVersion>>(url, Hosts, token).ConfigureAwait(false);
        foreach (var version in versions) { ValidateVersion(version); if (version.ProjectId != projectId) throw new InvalidDataException("Версия другого проекта."); }
        return versions.Where(x => instance is null || IsCompatible(x, instance)).OrderByDescending(x => x.DatePublished).ToArray();
    }
    public async Task<ModrinthVersion> VersionAsync(string id, CancellationToken token)
    {
        ValidateId(id);
        var version = await http.JsonAsync<ModrinthVersion>(Api + "version/" + id, Hosts, token, cache: false).ConfigureAwait(false);
        ValidateVersion(version);
        if (version.Id != id) throw new InvalidDataException("Сервер вернул другую версию.");
        return version;
    }
    public static bool SupportsClient(string? environment) => environment is "client_and_server" or "client_only" or "client_only_server_optional" or "singleplayer_only" or "server_only_client_optional" or "client_or_server" or "client_or_server_prefers_both";
    public static bool IsCompatible(ModrinthVersion version, GameInstance instance) => instance.Loader != ModLoader.Vanilla &&
        version.GameVersions.Contains(instance.VersionId, StringComparer.Ordinal) && version.Loaders.Contains(instance.Loader.ToString().ToLowerInvariant(), StringComparer.Ordinal) &&
        (version.Environment is null || SupportsClient(version.Environment));
    public static void ValidateCompatibility(ModrinthVersion version, ModrinthProject project, GameInstance instance)
    {
        ValidateVersion(version);
        if (project.Id != version.ProjectId || project.ProjectType != "mod" || !IsCompatible(version, instance) ||
            (version.Environment is null && project.ClientSide is not ("required" or "optional")))
            throw new InvalidOperationException($"«{project.Title}» {version.VersionNumber} не подходит для Minecraft {instance.VersionId} + {instance.Loader} на клиенте.");
    }
    public static void ValidateVersion(ModrinthVersion version)
    {
        if (version is null) throw new InvalidDataException("Пустая metadata версии Modrinth.");
        ValidateId(version.Id); ValidateId(version.ProjectId);
        if (version.GameVersions is null || version.Loaders is null || version.Dependencies is null || version.Files is null || version.Files.Count == 0 || version.Files.Count > 64 || version.Dependencies.Count > 256 || version.VersionNumber is null)
            throw new InvalidDataException("Неполная metadata версии Modrinth.");
        foreach (var dep in version.Dependencies)
        {
            if (dep is null || dep.DependencyType is not ("required" or "optional" or "embedded" or "incompatible")) throw new InvalidDataException("Неизвестная dependency metadata.");
            if (dep.ProjectId is not null) ValidateId(dep.ProjectId);
            if (dep.VersionId is not null) ValidateId(dep.VersionId);
        }
        foreach (var file in version.Files)
            if (file is null || file.Hashes is null || file.Filename is null || file.Url is null || file.Size <= 0) throw new InvalidDataException("Неполная metadata файла.");
    }
    public static void ValidateId(string? id)
    { if (id is not { Length: > 0 and <= 64 } || !id.All(char.IsAsciiLetterOrDigit)) throw new InvalidDataException("Некорректный Modrinth ID."); }
    public static ModrinthFile PrimaryFile(ModrinthVersion version, string extension)
    {
        ValidateVersion(version);
        if (version.Files.Any(x => x.FileType == "required-resource-pack"))
            throw new InvalidDataException("Эта версия требует отдельного resource pack; автоматическая установка таких версий пока не поддерживается.");
        var file = version.Files.FirstOrDefault(x => x.Primary) ?? version.Files[0];
        if (!file.Filename.EndsWith(extension, StringComparison.OrdinalIgnoreCase) || file.Filename.Contains('/') || file.Filename.Contains('\\') ||
            file.FileType is "sources-jar" or "dev-jar" or "javadoc-jar" or "signature")
            throw new InvalidDataException("У версии нет подходящего основного файла " + extension);
        LauncherHttp.ValidateHashes(file.Hashes);
        LauncherHttp.ValidateUrl(file.Url, ["cdn.modrinth.com"]);
        return file;
    }
}
