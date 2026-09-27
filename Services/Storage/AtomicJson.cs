using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NexLauncher.Services.Storage;

public static class AtomicJson
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static async Task<T?> ReadAsync<T>(string path, CancellationToken token)
    {
        SafePaths.NoLinks(path);
        if (!File.Exists(path)) return default;
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("Metadata слишком велика: " + path);
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, Options, token)
                ?? throw new InvalidDataException("Пустая metadata: " + path);
        }
        catch (JsonException ex) { throw new InvalidDataException("Повреждена metadata. Оригинал сохранён: " + path, ex); }
    }

    public static async Task WriteAsync<T>(string path, T value, CancellationToken token)
    {
        SafePaths.NoLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, Options, token);
                await stream.FlushAsync(token);
            }
            token.ThrowIfCancellationRequested();
            SafePaths.NoLinks(path);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
