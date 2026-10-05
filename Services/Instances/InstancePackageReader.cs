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
using NexLauncher.Services.Storage;

namespace NexLauncher.Services.Instances;

public sealed class ValidatedInstancePackage(ZipArchive archive, InstanceManifest manifest, string sha256) : IDisposable
{
    public ZipArchive Archive { get; } = archive;
    public InstanceManifest Manifest { get; } = manifest;
    public string Sha256 { get; } = sha256;
    public void Dispose() => Archive.Dispose();
}

public sealed class InstancePackageReader
{
    public async Task<InstanceImportPreview> PreviewAsync(string path, CancellationToken token)
    {
        using var package = await OpenAsync(path, token).ConfigureAwait(false);
        return new(package.Manifest, package.Sha256);
    }
    // Hold an unshared-write handle through import: preview/import never trust a replacement archive.
    public Task<ValidatedInstancePackage> OpenAsync(string path, CancellationToken token) => Task.Run(async () =>
    {
        token.ThrowIfCancellationRequested(); SafePaths.NoLinks(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        ZipArchive? archive = null;
        try
        {
            if (stream.Length > InstanceManifestService.MaxArchiveBytes) throw new InvalidDataException("Package превышает 2 ГиБ.");
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
            stream.Position = 0;
            archive = new ZipArchive(stream, ZipArchiveMode.Read);
            if (archive.Entries.Count is < 1 or > InstanceManifestService.MaxFiles + 1) throw new InvalidDataException("Слишком много записей в архиве.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                InstanceManifestService.RelativePath(entry.FullName);
                if (!names.Add(entry.FullName) || entry.FullName.EndsWith('/') || entry.Length < 0 || entry.Length > InstanceManifestService.MaxFileBytes ||
                    (entry.Length > 1024 * 1024 && entry.Length > Math.Max(1, entry.CompressedLength) * 200L) ||
                    ((entry.ExternalAttributes >> 16) & 0xF000) is not (0 or 0x8000) ||
                    (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Опасная, повторяющаяся или слишком сжатая archive entry.");
                total += entry.Length;
                if (total > InstanceManifestService.MaxTotalBytes) throw new InvalidDataException("Архив превышает допустимый распакованный размер.");
            }
            var manifestEntry = archive.GetEntry(InstanceManifestService.EntryName) ?? throw new InvalidDataException("NexLauncher manifest не найден.");
            if (manifestEntry.Length > InstanceManifestService.MaxManifestBytes) throw new InvalidDataException("Manifest превышает 2 МиБ.");
            await using var input = manifestEntry.Open();
            using var memory = new MemoryStream();
            await CopyVerifiedAsync(input, memory, manifestEntry.Length, null, token).ConfigureAwait(false);
            var bytes = memory.ToArray();
            InstanceManifest manifest;
            try
            {
                using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
                CheckJson(json.RootElement);
                if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("manifestVersion", out _)) throw new JsonException("Missing manifestVersion");
                manifest = JsonSerializer.Deserialize<InstanceManifest>(bytes, InstanceManifestService.Json) ?? throw new JsonException("null");
            }
            catch (JsonException ex) { throw new InvalidDataException("Некорректный NexLauncher manifest.", ex); }
            InstanceManifestService.Validate(manifest);
            if (archive.Entries.Count != manifest.Files.Count + 1) throw new InvalidDataException("Архив содержит неописанные или отсутствующие файлы.");
            foreach (var file in manifest.Files)
            {
                var entry = archive.GetEntry("files/" + file.Path) ?? throw new InvalidDataException("Bundled-файл отсутствует: " + file.Path);
                if (entry.Length != file.Size) throw new InvalidDataException("Размер entry не совпадает с manifest.");
                await using var content = entry.Open();
                await CopyVerifiedAsync(content, Stream.Null, file.Size, file.Sha256, token).ConfigureAwait(false);
            }
            return new ValidatedInstancePackage(archive, manifest, hash);
        }
        catch { archive?.Dispose(); await stream.DisposeAsync().ConfigureAwait(false); throw; }
    }, token);

    internal static async Task CopyVerifiedAsync(Stream source, Stream output, long expectedSize, string? expectedHash, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; long total = 0; int read;
        while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > expectedSize) throw new InvalidDataException("Распакованное содержимое превышает объявленный размер.");
            hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        if (total != expectedSize || expectedHash is not null && !Convert.ToHexString(hash.GetHashAndReset()).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Файл повреждён или изменился после preview; размер/hash не совпал.");
    }
    private static void CheckJson(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw new JsonException("Duplicate property"); CheckJson(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) CheckJson(item);
    }
}
