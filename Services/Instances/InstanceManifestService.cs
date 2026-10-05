using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexLauncher.Models;
using NexLauncher.Services.Loaders;
using NexLauncher.Services.Modrinth;
using NexLauncher.Services.Network;
using NexLauncher.Services.Storage;

namespace NexLauncher.Services.Instances;

/// <summary>Public, credential-free transfer format. No paths/URLs from it are executable commands.</summary>
public static class InstanceManifestService
{
    public const string EntryName = "nexlauncher.instance.json";
    public const int MaxFiles = 8192;
    public const int MaxManifestBytes = 2 * 1024 * 1024;
    public const long MaxFileBytes = 512L * 1024 * 1024;
    public const long MaxTotalBytes = 4L * 1024 * 1024 * 1024;
    public const long MaxArchiveBytes = 2L * 1024 * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<ModLoader>(allowIntegerValues: false) }
    };
    private static readonly HashSet<string> ForbiddenParts = new(StringComparer.OrdinalIgnoreCase)
    {
        "logs", "crash-reports", "screenshots", "saves", "worlds", "servers.dat", "servers.dat_old",
        ".nexlauncher", "accounts", "auth", "credentials", "tokens", "session", "sessions",
        "settings.json", "accounts.json", "usercache.json", "usernamecache.json", "launcher_profiles.json"
    };
    private static readonly string[] ExecutableExtensions = [".exe", ".dll", ".bat", ".cmd", ".ps1", ".sh", ".vbs", ".js", ".msi", ".com", ".lnk"];
    private static readonly HashSet<string> PrivateFileStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "tokens", "token", "password", "passwords", "credentials", "secrets", "secret", "accounts", "account", "auth", "session", "sessions",
        "access_token", "refresh_token", "access-token", "refresh-token", "client_secret", "client-secret", "microsoft-auth", "minecraft-auth", "api-keys"
    };

    public static string Name(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 60 || name.Any(char.IsControl))
            throw new InvalidDataException("Название сборки должно содержать 1–60 символов без управляющих символов.");
        return name.Trim();
    }
    public static void RelativePath(string path)
    {
        if (path is null || path.Length > 240) throw new InvalidDataException("Путь в package слишком длинный или отсутствует.");
        _ = SafePaths.Resolve(Path.Combine(Path.GetTempPath(), "NexLauncher-package-paths"), path);
    }
    public static bool IsAllowedContent(string path)
    {
        RelativePath(path);
        var parts = path.Split('/');
        if (parts.Length < 2 || parts.Any(ForbiddenParts.Contains)) return false;
        if (parts[0] is "config" or "defaultconfigs" && parts.Any(x => PrivateFileStems.Contains(Path.GetFileNameWithoutExtension(x)))) return false;
        if (ExecutableExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) return false;
        return parts[0] switch
        {
            "mods" => parts.Length == 2 && path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase),
            "config" or "defaultconfigs" => !path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase),
            "resourcepacks" or "shaderpacks" => true,
            _ => false
        };
    }
    public static void Validate(InstanceManifest manifest)
    {
        if (manifest is null || manifest.ManifestVersion != 1) throw new InvalidDataException("Неподдерживаемая версия NexLauncher manifest.");
        _ = Name(manifest.Name);
        LoaderCatalog.ValidateInstance(ToInstance(manifest, manifest.Name));
        if (manifest.MemoryMb is < 1024 or > 32768 || manifest.LauncherVersion is not { Length: > 0 and <= 64 } ||
            !LoaderCatalog.IsIdentifier(manifest.LauncherVersion) || manifest.ExportedUtc == default)
            throw new InvalidDataException("Некорректные настройки или export metadata.");
        if (manifest.Mods is null || manifest.Files is null || manifest.OmittedFiles is null || manifest.Mods.Count > 256 ||
            manifest.Files.Count > MaxFiles || manifest.OmittedFiles.Count > MaxFiles)
            throw new InvalidDataException("Неполный или слишком большой manifest.");
        if ((manifest.OriginalModpackProjectId is null) != (manifest.OriginalModpackVersionId is null))
            throw new InvalidDataException("Неполная ссылка на исходный modpack.");
        if (manifest.OriginalModpackProjectId is not null)
        { ModrinthService.ValidateId(manifest.OriginalModpackProjectId); ModrinthService.ValidateId(manifest.OriginalModpackVersionId); }
        var projects = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var mod in manifest.Mods)
        {
            if (mod is null) throw new InvalidDataException("Пустая запись мода.");
            ModrinthService.ValidateId(mod.ProjectId); ModrinthService.ValidateId(mod.VersionId);
            if (manifest.Loader == ModLoader.Vanilla || !projects.Add(mod.ProjectId) || mod.Filename is null || mod.Filename.Contains('/'))
                throw new InvalidDataException("Повтор проекта или managed-моды в Vanilla manifest.");
            var path = "mods/" + mod.Filename;
            if (!IsAllowedContent(path) || !paths.Add(path)) throw new InvalidDataException("Некорректное или повторяющееся имя мода.");
            LauncherHttp.ValidateHashes(mod.Hashes);
            if (mod.Hashes.Keys.Any(x => x is not ("sha512" or "sha1"))) throw new InvalidDataException("Неподдерживаемый алгоритм hash.");
            total += CheckedSize(mod.Size);
        }
        foreach (var file in manifest.Files)
        {
            if (file is null || !IsAllowedContent(file.Path) || !paths.Add(file.Path) ||
                file.Sha256 is not { Length: 64 } || !file.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("Небезопасный, повторяющийся или неполный путь bundled-файла.");
            total += CheckedSize(file.Size);
        }
        foreach (var path in paths)
        {
            var parent = path;
            while (parent.Contains('/'))
            { parent = parent[..parent.LastIndexOf('/')]; if (paths.Contains(parent)) throw new InvalidDataException("Конфликт файла и папки в package."); }
        }
        foreach (var omitted in manifest.OmittedFiles) RelativePath(omitted);
        if (total > MaxTotalBytes) throw new InvalidDataException("Суммарный размер содержимого превышает 4 ГиБ.");
    }
    private static long CheckedSize(long size) => size is < 0 or > MaxFileBytes ?
        throw new InvalidDataException("Размер файла превышает лимит 512 МиБ.") : size;
    public static GameInstance ToInstance(InstanceManifest manifest, string name) => new()
    {
        Name = Name(name), VersionId = manifest.MinecraftVersion, Loader = manifest.Loader,
        LoaderVersion = manifest.LoaderVersion, MemoryMb = manifest.MemoryMb, JavaPath = "",
        ModrinthProjectId = manifest.OriginalModpackProjectId, ModrinthVersionId = manifest.OriginalModpackVersionId
    };
}
