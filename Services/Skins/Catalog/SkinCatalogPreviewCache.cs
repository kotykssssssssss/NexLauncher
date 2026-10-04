using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using NexLauncher.Models;
using SkiaSharp;

namespace NexLauncher.Services.Skins.Catalog;

/// <summary>Bounded process-local thumbnails; clearing this cache never removes user skins.</summary>
public sealed class SkinCatalogPreviewCache
{
    public const int Capacity = 32;
    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTimeOffset Until, byte[] Png)> _frames = new();
    private readonly Func<DateTimeOffset> _now;
    public SkinCatalogPreviewCache(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.UtcNow);
    public Task<byte[]> GetAsync(SkinCatalogTexture texture, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        var key = Convert.ToHexStringLower(SHA256.HashData(texture.Image.Png)) + ":" + texture.Model;
        lock (_gate)
        {
            if (_frames.TryGetValue(key, out var hit) && hit.Until > _now()) return hit.Png.ToArray();
        }
        var full = new SkinPreviewRenderer().Render(texture.Image, texture.Model, -.35, 1, token);
        using var decoded = SKBitmap.Decode(full);
        using var small = decoded.Resize(new SKImageInfo(192, 192), new SKSamplingOptions(SKFilterMode.Nearest));
        using var image = SKImage.FromBitmap(small); using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = png.ToArray(); token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            foreach (var expired in _frames.Where(x => x.Value.Until <= _now()).Select(x => x.Key).ToArray()) _frames.Remove(expired);
            if (_frames.Count >= Capacity) _frames.Remove(_frames.MinBy(x => x.Value.Until).Key);
            _frames[key] = (_now().AddMinutes(5), bytes);
        }
        return bytes.ToArray();
    }, token);
    public int Count { get { lock (_gate) return _frames.Count; } }
}
