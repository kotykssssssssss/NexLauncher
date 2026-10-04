using System;
using System.Collections.Generic;

namespace NexLauncher.Models;

public sealed record SkinCatalogSource(string Id, string Name, string Description);
public sealed record SkinCatalogItem(string Id, string SourceId, string Name, SkinModel Model,
    bool Legacy, string Sha256, DateTimeOffset ImportedAt)
{
    public string ModelLabel => Model == SkinModel.Slim ? "Slim / Alex" : "Classic / Steve";
}
public sealed record SkinCatalogPage(IReadOnlyList<SkinCatalogItem> Items, int Total, string Notice = "");
public sealed record SkinCatalogTexture(SkinImage Image, SkinModel Model);
public sealed record StoredLibrarySkin(int FormatVersion, string Id, string Name, SkinModel Model,
    bool Legacy, string Sha256, DateTimeOffset ImportedAt);
