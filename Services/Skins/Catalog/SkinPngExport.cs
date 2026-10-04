using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NexLauncher.Models;
using NexLauncher.Services.Storage;

namespace NexLauncher.Services.Skins.Catalog;

public sealed class SkinPngExport(SkinValidator validator)
{
    public Task SaveAsync(SkinImage image, string destination, CancellationToken token) => Task.Run(async () =>
    {
        if (!Path.IsPathFullyQualified(destination) || !destination.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            throw new SkinException("Выбери полный путь к PNG-файлу.");
        SafePaths.NoLinks(destination);
        var clean = validator.Validate(image.Png, token);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
            { await stream.WriteAsync(clean.Png, token).ConfigureAwait(false); await stream.FlushAsync(token).ConfigureAwait(false); }
            token.ThrowIfCancellationRequested(); SafePaths.NoLinks(destination);
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }, token);
}
