using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NexLauncher.Services.Storage;

namespace NexLauncher.Services.QuickCss;

public sealed record QuickCssFileSnapshot(string Text, string Revision);

/// <summary>Bounded UTF-8 editing with atomic saves and external-change detection.</summary>
public sealed class QuickCssEditorFiles
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly SemaphoreSlim _gate = new(1, 1);
    public Task<QuickCssFileSnapshot> LoadAsync(string path, CancellationToken token) => RunAsync<QuickCssFileSnapshot>(async () =>
    {
        ValidatePath(path);
        var bytes = await QuickCssImageLoader.ReadBoundedAsync(path, QuickCssParser.MaximumInputBytes, token);
        try { return new(Utf8.GetString(bytes).TrimStart('\ufeff'), Hash(bytes)); }
        catch (DecoderFallbackException) { throw new InvalidDataException("Quick CSS должен быть текстовым файлом UTF-8."); }
    }, token);

    public Task<QuickCssFileSnapshot> SaveAsync(string path, string text, string expectedRevision, CancellationToken token) => RunAsync<QuickCssFileSnapshot>(async () =>
    {
        ValidatePath(path); SafePaths.NoLinks(path);
        if (text.Length > QuickCssParser.MaximumInputBytes) throw new InvalidDataException("Quick CSS ограничен 128 КиБ. Файл не изменён.");
        byte[] bytes;
        try { bytes = Utf8.GetBytes(text); }
        catch (EncoderFallbackException) { throw new InvalidDataException("Текст содержит некорректные Unicode-символы."); }
        if (bytes.Length > QuickCssParser.MaximumInputBytes) throw new InvalidDataException("Quick CSS ограничен 128 КиБ. Файл не изменён.");
        await CheckRevisionAsync(path, expectedRevision, token);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
            { await stream.WriteAsync(bytes, token); await stream.FlushAsync(token); }
            await CheckRevisionAsync(path, expectedRevision, token);
            token.ThrowIfCancellationRequested(); SafePaths.NoLinks(path);
            File.Move(temporary, path, true);
            return new(text, Hash(bytes));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }, token);

    private async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { return await Task.Run(action, token); }
        finally { _gate.Release(); }
    }
    private static async Task CheckRevisionAsync(string path, string expected, CancellationToken token)
    {
        if (Hash(await QuickCssImageLoader.ReadBoundedAsync(path, QuickCssParser.MaximumInputBytes, token)) != expected)
            throw new InvalidDataException("Файл изменён вне этого окна. Скопируй свой черновик и перечитай файл; чужие изменения не перезаписаны.");
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void ValidatePath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !path.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Выбери .css файл с полным путём.");
    }
}
