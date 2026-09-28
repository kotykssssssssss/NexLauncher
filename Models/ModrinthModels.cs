using System;
using System.Collections.Generic;
using System.Linq;

namespace NexLauncher.Models;

public sealed class ModrinthSearchResult
{
    public List<ModrinthHit> Hits { get; set; } = new();
    public int Offset { get; set; }
    public int Limit { get; set; }
    public int TotalHits { get; set; }
}
public sealed class ModrinthHit
{
    public string ProjectId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public string ProjectType { get; set; } = "";
    public string? IconUrl { get; set; }
    public long Downloads { get; set; }
    public string[] Versions { get; set; } = [];
    public string[] Categories { get; set; } = [];
    public string? Organization { get; set; }
    public DateTimeOffset DateModified { get; set; }
    public string Credit => (Organization is { Length: > 0 } ? Organization : Author) + " · " + Downloads.ToString("N0") + " загрузок";
    public string Tags => string.Join(" · ", Categories.Where(x => x is not ("fabric" or "forge" or "neoforge" or "quilt")).Take(5));
    public string Updated => DateModified == default ? "" : "Обновлён " + DateModified.ToString("dd.MM.yyyy");
    public string Compatibility => string.Join(", ", Categories.Where(x => x is "fabric" or "forge" or "neoforge")) + " · " + string.Join(", ", Versions.TakeLast(6));
}
public sealed class ModrinthProject
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Body { get; set; } = "";
    public string ProjectType { get; set; } = "";
    public string ClientSide { get; set; } = "unknown";
    public string[] Environment { get; set; } = [];
    public string[] GameVersions { get; set; } = [];
    public string[] Loaders { get; set; } = [];
    public string[] Categories { get; set; } = [];
    public DateTimeOffset Published { get; set; }
    public DateTimeOffset Updated { get; set; }
    public ModrinthLicense? License { get; set; }
    public string? IconUrl { get; set; }
    public long Downloads { get; set; }
    public string? Organization { get; set; }
    public string? IssuesUrl { get; set; }
    public string? SourceUrl { get; set; }
    public string? WikiUrl { get; set; }
    public string? DiscordUrl { get; set; }
    public List<ModrinthDonation>? DonationUrls { get; set; }
}
public sealed class ModrinthDonation
{
    public string Platform { get; set; } = "";
    public string Url { get; set; } = "";
}
public sealed class ModrinthTeamMember
{
    public ModrinthUser User { get; set; } = new();
    public string Role { get; set; } = "";
    public int Ordering { get; set; }
}
public sealed class ModrinthUser
{
    public string Username { get; set; } = "";
    public string? Name { get; set; }
}
public sealed class ModrinthLicense
{
    public string Id { get; set; } = "";
    public string? Url { get; set; }
}
public sealed class ModrinthVersion
{
    public string Id { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string Name { get; set; } = "";
    public string VersionNumber { get; set; } = "";
    public string VersionType { get; set; } = "release";
    public DateTimeOffset DatePublished { get; set; }
    public string[] GameVersions { get; set; } = [];
    public string[] Loaders { get; set; } = [];
    public string? Environment { get; set; }
    public List<ModrinthDependency> Dependencies { get; set; } = new();
    public List<ModrinthFile> Files { get; set; } = new();
    public override string ToString() => VersionNumber + " · " + VersionType + " · " + string.Join(", ", GameVersions) + " · " + string.Join(", ", Loaders);
}
public sealed class ModrinthFile
{
    public string Url { get; set; } = "";
    public string Filename { get; set; } = "";
    public Dictionary<string, string> Hashes { get; set; } = new();
    public long Size { get; set; }
    public bool Primary { get; set; }
    public string? FileType { get; set; }
}
public sealed class ModrinthDependency
{
    public string? ProjectId { get; set; }
    public string? VersionId { get; set; }
    public string? FileName { get; set; }
    public string DependencyType { get; set; } = "";
}
public sealed class InstalledMods
{
    public int FormatVersion { get; set; } = 1;
    public List<InstalledMod> Projects { get; set; } = new();
}
public sealed class InstalledMod
{
    public string Title { get; set; } = "";
    public ModrinthVersion Version { get; set; } = new();
    public string FileName { get; set; } = "";
    public Dictionary<string, string> Hashes { get; set; } = new();
    public bool Explicit { get; set; }
    public string DisplayName => Title + " · " + Version.VersionNumber + (Explicit ? "" : " · зависимость");
}
