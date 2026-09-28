using System;
using System.Collections.Generic;

namespace NexLauncher.Models;

public sealed record ModrinthSearchOptions(string Minecraft = "", string Loader = "", string Category = "", string Environment = "", string Sort = "relevance");
public sealed record FilterChoice(string Value, string Label)
{
    public override string ToString() => Label;
}
public sealed class ModrinthCategory
{
    public string Name { get; set; } = "";
    public string ProjectType { get; set; } = "";
}
public sealed class ModrinthGameVersion
{
    public string Version { get; set; } = "";
    public string VersionType { get; set; } = "";
    public DateTimeOffset Date { get; set; }
}
public sealed record ModrinthFilterCatalog(IReadOnlyList<ModrinthCategory> Categories, IReadOnlyList<ModrinthGameVersion> Versions);
