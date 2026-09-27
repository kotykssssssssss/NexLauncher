using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using NexLauncher.Models;
using NexLauncher.Services.Loaders;
using NexLauncher.Services.Network;
using NexLauncher.Services.Storage;

namespace NexLauncher.Services.Modrinth;

public sealed class MrPackIndex
{
    public int FormatVersion { get; set; }
    public string Game { get; set; } = "";
    public string VersionId { get; set; } = "";
    public string Name { get; set; } = "";
    public Dictionary<string, string> Dependencies { get; set; } = new();
    public List<MrPackFile> Files { get; set; } = new();
}
public sealed class MrPackFile
{
    public string Path { get; set; } = "";
    public Dictionary<string, string> Hashes { get; set; } = new();
    public Dictionary<string, string>? Env { get; set; }
    public string[] Downloads { get; set; } = [];
    public long FileSize { get; set; }
}
public sealed record PackOverride(string Entry, string Destination);
public sealed record MrPackPlan(GameInstance Instance, IReadOnlyList<MrPackFile> Files, IReadOnlyList<MrPackFile> OptionalFiles, IReadOnlyList<PackOverride> Overrides);

public static class MrPackPlanner
{
    // Packs may customize game content, never the launcher's metadata, Java, loader profiles or executables.
    public static string ValidateContentPath(string root, string relative)
    {
        var result = SafePaths.Resolve(root, relative);
        var first = relative.Split('/')[0];
        if (first.StartsWith('.') || new[] { "versions", "libraries", "runtime", "assets", "natives" }.Contains(first, StringComparer.OrdinalIgnoreCase) ||
            relative.StartsWith("launcher_", StringComparison.OrdinalIgnoreCase) ||
            new[] { ".exe", ".dll", ".bat", ".cmd", ".ps1", ".sh", ".lnk" }.Contains(System.IO.Path.GetExtension(relative), StringComparer.OrdinalIgnoreCase) ||
            (relative.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && first != "mods"))
            throw new InvalidDataException("Modpack пытается изменить служебный или исполняемый файл: " + relative);
        return result;
    }

    public static MrPackPlan Read(ZipArchive zip, string root)
    {
        if (zip.Entries.Count > 20000) throw new InvalidDataException("Слишком много файлов в modpack.");
        long unpacked = 0;
        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.TrimEnd('/');
            _ = SafePaths.Resolve(root, name);
            if (!entries.Add(name) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || ((FileAttributes)entry.ExternalAttributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Дубли или ссылки в архиве modpack.");
            unpacked = checked(unpacked + entry.Length);
            if (entry.Length > 512L * 1024 * 1024 || unpacked > 4L * 1024 * 1024 * 1024) throw new InvalidDataException("Распакованный modpack слишком велик.");
        }
        var indexEntry = zip.GetEntry("modrinth.index.json") ?? throw new InvalidDataException("В архиве нет modrinth.index.json.");
        if (indexEntry.Length > 8 * 1024 * 1024) throw new InvalidDataException("Индекс modpack слишком велик.");
        using var stream = indexEntry.Open();
        var index = JsonSerializer.Deserialize<MrPackIndex>(stream, AtomicJson.Options) ?? throw new InvalidDataException("Пустой индекс modpack.");
        if (index.FormatVersion != 1 || index.Game != "minecraft" || string.IsNullOrWhiteSpace(index.VersionId) || index.Files is null || index.Dependencies is null || index.Files.Count > 10000 ||
            !index.Dependencies.TryGetValue("minecraft", out var minecraft)) throw new InvalidDataException("Неподдерживаемый формат modpack.");
        var instance = new GameInstance { Name = index.Name, VersionId = minecraft };
        foreach (var pair in index.Dependencies.Where(x => x.Key != "minecraft"))
        {
            if (instance.Loader != ModLoader.Vanilla) throw new InvalidDataException("Modpack требует несколько загрузчиков.");
            instance.Loader = pair.Key switch { "fabric-loader" => ModLoader.Fabric, "forge" => ModLoader.Forge, "neoforge" => ModLoader.NeoForge, _ => throw new InvalidDataException("Не поддерживается загрузчик modpack: " + pair.Key) };
            instance.LoaderVersion = pair.Value;
        }
        LoaderCatalog.ValidateInstance(instance);
        var files = new List<MrPackFile>(); var optional = new List<MrPackFile>();
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long downloads = 0;
        foreach (var file in index.Files)
        {
            if (file is null || file.Path is null) throw new InvalidDataException("Неполная запись файла в modpack.");
            ValidateContentPath(root, file.Path);
            if (!destinations.Add(file.Path)) throw new InvalidDataException("Дубли путей в индексе modpack.");
            if (file.Hashes is null || !file.Hashes.ContainsKey("sha512") || !file.Hashes.ContainsKey("sha1")) throw new InvalidDataException("Modpack должен содержать SHA-512 и SHA-1.");
            LauncherHttp.ValidateHashes(file.Hashes);
            if (file.Downloads is null || file.Downloads.Length == 0 || file.Downloads.Length > 8) throw new InvalidDataException("Нет адреса файла modpack.");
            foreach (var url in file.Downloads)
            { if (url is null) throw new InvalidDataException("Пустой URL файла modpack."); LauncherHttp.ValidateUrl(url, LauncherHttp.PackHosts); }
            if (file.FileSize < 0 || file.FileSize > 512L * 1024 * 1024 || (downloads += file.FileSize) > 8L * 1024 * 1024 * 1024) throw new InvalidDataException("Modpack превышает лимит загрузок (8 ГБ).");
            if (file.Env is not null && (!file.Env.TryGetValue("client", out _) || !file.Env.TryGetValue("server", out _) || file.Env.Values.Any(x => x is not ("required" or "optional" or "unsupported"))))
                throw new InvalidDataException("Неизвестный environment в modpack.");
            var client = file.Env?.GetValueOrDefault("client") ?? "required";
            if (client == "required") files.Add(file);
            else if (client == "optional") optional.Add(file);
        }
        var overrides = new Dictionary<string, PackOverride>(StringComparer.OrdinalIgnoreCase);
        foreach (var prefix in new[] { "overrides/", "client-overrides/" })
            foreach (var entry in zip.Entries.Where(x => x.FullName.StartsWith(prefix, StringComparison.Ordinal) && !x.FullName.EndsWith('/')))
            {
                var relative = entry.FullName[prefix.Length..];
                ValidateContentPath(root, relative);
                overrides[relative] = new(entry.FullName, relative); // client layer wins as specified
            }
        return new(instance, files, optional, overrides.Values.ToArray());
    }
}
