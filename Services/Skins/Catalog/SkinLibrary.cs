using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NexLauncher.Models;
using NexLauncher.Services.Storage;

namespace NexLauncher.Services.Skins.Catalog;

/// <summary>Explicitly imported user skins. This is durable user data, not an evictable web cache.</summary>
public sealed class SkinLibrary(string dataDirectory, SkinValidator validator) : ISkinCatalogSource
{
    public const int MaxEntries = 256;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public string DirectoryPath => SafePaths.Resolve(dataDirectory, "skin-library");
    public SkinCatalogSource Source { get; } = new("local", "Моя библиотека",
        "Сохранённые PNG на этом устройстве. Автоматический онлайн-каталог пока не подключён.");

    public Task<SkinCatalogPage> SearchAsync(string query, int offset, int limit, CancellationToken token) => Task.Run(async () =>
    {
        if (query.Length > 100 || offset < 0 || limit is < 1 or > 48) throw new SkinException("Некорректный запрос библиотеки скинов.");
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var (entries, damaged) = await ListAsync(token).ConfigureAwait(false);
            var filtered = entries.Where(x => x.Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.ImportedAt).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
            return new SkinCatalogPage(filtered.Skip(offset).Take(limit).Select(Item).ToArray(), filtered.Length,
                damaged == 0 ? "" : $"Не удалось прочитать {damaged} записей. Оригинальные файлы сохранены для восстановления.");
        }
        finally { _gate.Release(); }
    }, token);

    public Task<SkinCatalogTexture> ReadAsync(SkinCatalogItem item, CancellationToken token) => Task.Run(async () =>
    {
        CheckSource(item);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var metadata = await MetadataAsync(item.Id, token).ConfigureAwait(false);
            var bytes = await PngAsync(metadata, token).ConfigureAwait(false);
            var image = validator.Validate(bytes, token) with { Legacy = metadata.Legacy };
            SkinValidator.ValidateModel(image, metadata.Model);
            return new SkinCatalogTexture(image, metadata.Model);
        }
        finally { _gate.Release(); }
    }, token);

    public async Task<SkinCatalogItem> ImportAsync(string path, SkinModel model, CancellationToken token)
    {
        var image = await validator.ImportAsync(path, token).ConfigureAwait(false);
        if (image.Legacy) model = SkinModel.Classic;
        SkinValidator.ValidateModel(image, model);
        var title = Path.GetFileNameWithoutExtension(path);
        title = new string(title.Where(c => !char.IsControl(c)).Take(80).ToArray()).Trim();
        if (title.Length == 0) title = "Мой скин";
        return await Task.Run(async () =>
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var (entries, _) = await ListAsync(token).ConfigureAwait(false);
                var hash = Convert.ToHexStringLower(SHA256.HashData(image.Png));
                var duplicate = entries.FirstOrDefault(x => x.Sha256 == hash && x.Model == model && x.Legacy == image.Legacy);
                if (duplicate is not null)
                {
                    // A damaged existing image must not be silently considered a successful import.
                    await PngAsync(duplicate, token).ConfigureAwait(false);
                    return Item(duplicate);
                }
                if (OwnedDirectories().Count() >= MaxEntries) throw new SkinException($"В библиотеке максимум {MaxEntries} скинов. Удали ненужную запись перед импортом.");
                var id = Guid.NewGuid().ToString("N");
                var staging = SafePaths.Resolve(DirectoryPath, ".nex-stage-" + id);
                var final = EntryPath(id);
                Directory.CreateDirectory(staging);
                try
                {
                    await File.WriteAllBytesAsync(SafePaths.Resolve(staging, "skin.png"), image.Png, token).ConfigureAwait(false);
                    var metadata = new StoredLibrarySkin(1, id, title, model, image.Legacy, hash, DateTimeOffset.UtcNow);
                    await AtomicJson.WriteAsync(SafePaths.Resolve(staging, "skin.json"), metadata, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested(); SafePaths.NoLinks(final);
                    Directory.Move(staging, final);
                    return Item(metadata);
                }
                finally { SafePaths.DeleteStaging(DirectoryPath, staging); }
            }
            finally { _gate.Release(); }
        }, token).ConfigureAwait(false);
    }

    public Task<SkinCatalogItem> SetModelAsync(SkinCatalogItem item, SkinModel model, CancellationToken token) => Task.Run(async () =>
    {
        CheckSource(item);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var metadata = await MetadataAsync(item.Id, token).ConfigureAwait(false);
            var image = validator.Validate(await PngAsync(metadata, token).ConfigureAwait(false), token) with { Legacy = metadata.Legacy };
            SkinValidator.ValidateModel(image, model);
            metadata = metadata with { Model = model };
            await AtomicJson.WriteAsync(SafePaths.Resolve(EntryPath(item.Id), "skin.json"), metadata, token).ConfigureAwait(false);
            return Item(metadata);
        }
        finally { _gate.Release(); }
    }, token);

    public Task<string> RemoveAsync(SkinCatalogItem item, CancellationToken token) => Task.Run(async () =>
    {
        CheckSource(item);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var metadata = await MetadataAsync(item.Id, token).ConfigureAwait(false);
            var directory = EntryPath(item.Id);
            var png = SafePaths.Resolve(directory, "skin.png");
            var owned = false;
            try { await PngAsync(metadata, token).ConfigureAwait(false); owned = true; }
            catch (SkinException) { } catch (FileNotFoundException) { }
            token.ThrowIfCancellationRequested();
            File.Delete(SafePaths.Resolve(directory, "skin.json"));
            // Metadata is already committed as removed. An orphan is safer than a misleading failed-delete UI.
            try
            {
                if (owned) File.Delete(png);
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
            catch (IOException) { return "Запись удалена, но очистить PNG не удалось. Файл оставлен; скины аккаунтов не изменились."; }
            catch (UnauthorizedAccessException) { return "Запись удалена, но очистить PNG не удалось. Файл оставлен; скины аккаунтов не изменились."; }
            return owned ? "Скин удалён из библиотеки. Скины аккаунтов не изменились." :
                "Запись удалена. Изменённый или отсутствующий PNG не удалялся; скины аккаунтов не изменились.";
        }
        finally { _gate.Release(); }
    }, token);

    private IEnumerable<string> OwnedDirectories()
    {
        SafePaths.NoLinks(DirectoryPath);
        return !Directory.Exists(DirectoryPath) ? [] : Directory.EnumerateDirectories(DirectoryPath)
            .Where(x => Regex.IsMatch(Path.GetFileName(x), "^[0-9a-f]{32}$") && File.Exists(Path.Combine(x, "skin.json"))).Take(MaxEntries + 1);
    }
    private async Task<(List<StoredLibrarySkin> Entries, int Damaged)> ListAsync(CancellationToken token)
    {
        var entries = new List<StoredLibrarySkin>(); var damaged = 0;
        foreach (var directory in OwnedDirectories())
        {
            token.ThrowIfCancellationRequested();
            try { entries.Add(await MetadataAsync(Path.GetFileName(directory), token).ConfigureAwait(false)); }
            catch (IOException) { damaged++; } catch (InvalidDataException) { damaged++; }
            catch (SkinException) { damaged++; } catch (UnauthorizedAccessException) { damaged++; }
        }
        return (entries, damaged);
    }
    private async Task<StoredLibrarySkin> MetadataAsync(string id, CancellationToken token)
    {
        var path = SafePaths.Resolve(EntryPath(id), "skin.json");
        if (File.Exists(path) && new FileInfo(path).Length > 16 * 1024) throw new SkinException("Запись скина слишком велика. Оригинал сохранён.");
        var data = await AtomicJson.ReadAsync<StoredLibrarySkin>(path, token).ConfigureAwait(false);
        if (data is null || data.FormatVersion != 1 || data.Id != id || string.IsNullOrWhiteSpace(data.Name) || data.Name.Length > 80 ||
            data.Name.Any(char.IsControl) || !Enum.IsDefined(data.Model) || data.Legacy && data.Model != SkinModel.Classic ||
            !Regex.IsMatch(data.Sha256 ?? "", "^[0-9a-f]{64}$") || data.ImportedAt == default)
            throw new SkinException("Запись скина повреждена или имеет неподдерживаемый формат. Оригинал сохранён.");
        return data;
    }
    private async Task<byte[]> PngAsync(StoredLibrarySkin data, CancellationToken token)
    {
        await using var stream = File.OpenRead(SafePaths.Resolve(EntryPath(data.Id), "skin.png"));
        var bytes = await SkinValidator.ReadBoundedAsync(stream, SkinValidator.MaxBytes, token).ConfigureAwait(false);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != data.Sha256) throw new SkinException("PNG в библиотеке повреждён. Оригинальный файл сохранён.");
        return bytes;
    }
    private string EntryPath(string id)
    {
        if (string.IsNullOrEmpty(id) || !Regex.IsMatch(id, "^[0-9a-f]{32}$")) throw new SkinException("Некорректный идентификатор скина.");
        return SafePaths.Resolve(DirectoryPath, id);
    }
    private void CheckSource(SkinCatalogItem item)
    {
        if (item.SourceId != Source.Id) throw new SkinException("Этот скин относится к другому источнику.");
    }
    private SkinCatalogItem Item(StoredLibrarySkin data) => new(data.Id, Source.Id, data.Name, data.Model, data.Legacy, data.Sha256, data.ImportedAt);
}
