using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NexLauncher.Models;
using NexLauncher.Services.Storage;

namespace NexLauncher.Services.Skins;

public sealed class SkinStorage(string dataDirectory, SkinValidator validator)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public string AccountDirectory(LauncherAccount account) => SafePaths.Resolve(Path.Combine(dataDirectory, "skins"),
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(account.Type + ":" + account.Id))));
    public async Task<AccountSkin> LoadAsync(LauncherAccount account, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { return await LoadCoreAsync(account, token); } finally { _gate.Release(); }
    }
    private async Task<StoredSkin?> MetadataAsync(LauncherAccount account, CancellationToken token)
    {
        var path = SafePaths.Resolve(AccountDirectory(account), "skin.json");
        if (File.Exists(path) && new FileInfo(path).Length > 16 * 1024) throw new SkinException("Metadata скина повреждена. Оригинальный файл сохранён.");
        var metadata = await AtomicJson.ReadAsync<StoredSkin>(path, token);
        if (metadata is not null && (metadata.FormatVersion != 1 || metadata.AccountId != account.Id || metadata.AccountType != account.Type ||
            !Enum.IsDefined(metadata.Model) || !Regex.IsMatch(metadata.FileName ?? "", "^[0-9a-f]{32}\\.png$") ||
            !Regex.IsMatch(metadata.Sha256 ?? "", "^[0-9a-f]{64}$")))
            throw new SkinException("Metadata скина повреждена или относится к другому аккаунту. Оригинал сохранён.");
        return metadata;
    }
    private async Task<AccountSkin> LoadCoreAsync(LauncherAccount account, CancellationToken token)
    {
        var metadata = await MetadataAsync(account, token);
        if (metadata is null) return new(null, SkinModel.Classic, true);
        var path = SafePaths.Resolve(AccountDirectory(account), metadata.FileName);
        await using var stream = File.OpenRead(path);
        var bytes = await SkinValidator.ReadBoundedAsync(stream, SkinValidator.MaxBytes, token);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != metadata.Sha256) throw new SkinException("Сохранённый скин повреждён. Файл не изменён.");
        var image = await Task.Run(() => validator.Validate(bytes, token) with { Legacy = metadata.Legacy }, token);
        SkinValidator.ValidateModel(image, metadata.Model);
        return new(image, metadata.Model, true);
    }
    public async Task<AccountSkin> SaveAsync(LauncherAccount account, SkinImage image, SkinModel model, CancellationToken token)
    {
        SkinValidator.ValidateModel(image, model);
        // Revalidate at the service boundary; never trust caller-constructed pixel/PNG DTOs.
        var clean = await Task.Run(() => validator.Validate(image.Png, token), token);
        clean = clean with { Legacy = clean.Legacy || image.Legacy };
        SkinValidator.ValidateModel(clean, model);
        await _gate.WaitAsync(token);
        try
        {
            var old = await MetadataAsync(account, token); var directory = AccountDirectory(account);
            Directory.CreateDirectory(directory);
            var name = Guid.NewGuid().ToString("N") + ".png"; var path = SafePaths.Resolve(directory, name);
            var committed = false; var created = false;
            try
            {
                await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    created = true;
                    await stream.WriteAsync(clean.Png, token);
                    await stream.FlushAsync(token);
                }
                var metadata = new StoredSkin(1, account.Id, account.Type, model, name, Convert.ToHexStringLower(SHA256.HashData(clean.Png)), clean.Legacy);
                await AtomicJson.WriteAsync(SafePaths.Resolve(directory, "skin.json"), metadata, token); committed = true;
            }
            finally { if (created && !committed && File.Exists(path)) File.Delete(path); }
            if (old is not null) DeleteOwnedPng(directory, old.FileName);
            return new(clean, model, true);
        }
        finally { _gate.Release(); }
    }
    public async Task<AccountSkin> ResetAsync(LauncherAccount account, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var old = await MetadataAsync(account, token); token.ThrowIfCancellationRequested();
            var directory = AccountDirectory(account);
            File.Delete(SafePaths.Resolve(directory, "skin.json"));
            if (old is not null) DeleteOwnedPng(directory, old.FileName);
            return new(null, SkinModel.Classic, true);
        }
        finally { _gate.Release(); }
    }
    private static void DeleteOwnedPng(string directory, string name)
    {
        try { File.Delete(SafePaths.Resolve(directory, name)); }
        catch (IOException) { } catch (UnauthorizedAccessException) { } // An orphan is safer than undoing committed metadata.
    }
}
