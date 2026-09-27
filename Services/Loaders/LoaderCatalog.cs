using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using NexLauncher.Models;
using NexLauncher.Services.Network;

namespace NexLauncher.Services.Loaders;

public sealed record LoaderRelease(string Version, string MinecraftVersion, bool Stable = true)
{
    public override string ToString() => Version + (Stable ? "" : " · beta");
}
public interface ILoaderCatalog
{
    Task<IReadOnlyList<LoaderRelease>> GetVersionsAsync(ModLoader loader, string minecraft, CancellationToken token);
}

public sealed class LoaderCatalog(LauncherHttp http) : ILoaderCatalog
{
    public async Task<IReadOnlyList<LoaderRelease>> GetVersionsAsync(ModLoader loader, string minecraft, CancellationToken token)
    {
        ValidateIdentifier(minecraft);
        if (loader == ModLoader.Vanilla) return Array.Empty<LoaderRelease>();
        var url = loader switch
        {
            ModLoader.Fabric => "https://meta.fabricmc.net/v2/versions/loader/" + Uri.EscapeDataString(minecraft),
            ModLoader.Forge => "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml",
            ModLoader.NeoForge => "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml",
            _ => throw new InvalidDataException("Неизвестный загрузчик.")
        };
        var data = await http.GetBytesAsync(url, LauncherHttp.LoaderHosts, token).ConfigureAwait(false);
        return await Task.Run(() => ParseVersions(loader, minecraft, Encoding.UTF8.GetString(data)), token).ConfigureAwait(false);
    }

    public static IReadOnlyList<LoaderRelease> ParseVersions(ModLoader loader, string minecraft, string metadata)
    {
        ValidateIdentifier(minecraft);
        try
        {
            if (loader == ModLoader.Fabric)
            {
                using var json = JsonDocument.Parse(metadata);
                return json.RootElement.EnumerateArray().Select(x => x.GetProperty("loader"))
                    .Select(x => new LoaderRelease(x.GetProperty("version").GetString()!, minecraft, x.GetProperty("stable").GetBoolean()))
                    .Where(x => IsIdentifier(x.Version)).DistinctBy(x => x.Version).ToArray();
            }
            if (loader == ModLoader.Vanilla) return Array.Empty<LoaderRelease>();
            using var reader = XmlReader.Create(new StringReader(metadata), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var versions = XDocument.Load(reader).Descendants("version").Select(x => x.Value).Where(IsIdentifier);
            if (loader == ModLoader.Forge)
            {
                // Official headless client installers use the 1.13+ install_profile schema.
                if (!Version.TryParse(minecraft, out var mc) || mc < new Version(1, 13)) return Array.Empty<LoaderRelease>();
                return versions.Where(x => x.StartsWith(minecraft + "-", StringComparison.Ordinal))
                    .Select(x => new LoaderRelease(x[(minecraft.Length + 1)..], minecraft)).OrderByDescending(x => SortableVersion(x.Version)).DistinctBy(x => x.Version).ToArray();
            }
            if (loader != ModLoader.NeoForge) throw new InvalidDataException("Неизвестный загрузчик.");
            return versions.Where(x => NeoMinecraftVersion(x) == minecraft)
                .Select(x => new LoaderRelease(x, minecraft, !x.Contains('-'))).OrderByDescending(x => SortableVersion(x.Version)).DistinctBy(x => x.Version).ToArray();
        }
        catch (Exception ex) when (ex is JsonException or XmlException or InvalidOperationException or KeyNotFoundException)
        { throw new InvalidDataException("Не удалось прочитать официальный список загрузчиков.", ex); }
    }

    public static string? NeoMinecraftVersion(string version)
    {
        // NeoForged's published versioning policies: 20.2 / 21.x and 26.x four-part releases.
        var match = Regex.Match(version, @"^(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?(?:-beta)?$", RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        if (!int.TryParse(match.Groups[1].Value, out var major)) return null;
        if (major >= 26 && match.Groups[4].Success)
            return match.Groups[1].Value + "." + match.Groups[2].Value + (match.Groups[3].Value == "0" ? "" : "." + match.Groups[3].Value);
        if (major is 20 or 21 && !match.Groups[4].Success)
            return "1." + match.Groups[1].Value + (match.Groups[2].Value == "0" ? "" : "." + match.Groups[2].Value);
        return null;
    }
    private static Version SortableVersion(string version) => Version.TryParse(version.Split('-')[0], out var parsed) ? parsed : new Version(0, 0);
    public static bool IsIdentifier(string? value) => value is { Length: > 0 and <= 128 } && value is not "." and not ".." &&
        !value.EndsWith('.') && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+');
    public static void ValidateIdentifier(string? value)
    { if (!IsIdentifier(value)) throw new InvalidDataException("Недопустимый идентификатор версии."); }
    public static void ValidateInstance(GameInstance instance)
    {
        if (instance.FormatVersion is < 1 or > 2 || !Enum.IsDefined(instance.Loader)) throw new InvalidDataException("Неизвестный формат сборки или загрузчик.");
        ValidateIdentifier(instance.VersionId);
        if (instance.Loader != ModLoader.Vanilla) ValidateIdentifier(instance.LoaderVersion);
        else if (!string.IsNullOrEmpty(instance.LoaderVersion)) throw new InvalidDataException("Vanilla не имеет loader version.");
    }
}
