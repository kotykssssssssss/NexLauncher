using System.Threading;
using System.Threading.Tasks;
using NexLauncher.Models;

namespace NexLauncher.Services.Skins.Catalog;

/// <summary>A source supplies public skin data, never account credentials or account mutations.</summary>
public interface ISkinCatalogSource
{
    SkinCatalogSource Source { get; }
    Task<SkinCatalogPage> SearchAsync(string query, int offset, int limit, CancellationToken token);
    Task<SkinCatalogTexture> ReadAsync(SkinCatalogItem item, CancellationToken token);
}
