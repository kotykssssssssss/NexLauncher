using System;
using System.IO;
using System.Text;

namespace NexLauncher;

/// <summary>Public product information. The privacy notice is bundled, not fetched from a server.</summary>
public static class ApplicationInformation
{
    public const string SourceUrl = "https://github.com/kotykssssssssss/NexLauncher";
    public const string IssuesUrl = SourceUrl + "/issues";
    private static readonly Lazy<string> Privacy = new(() =>
    {
        using var source = typeof(ApplicationInformation).Assembly.GetManifestResourceStream("NexLauncher.Privacy");
        if (source is null) return "Политика конфиденциальности недоступна в этой сборке.";
        using var reader = new StreamReader(source, Encoding.UTF8);
        return reader.ReadToEnd();
    });
    public static string PrivacyText => Privacy.Value;
    public static string? Link(string? key) => key switch { "source" => SourceUrl, "issues" => IssuesUrl, _ => null };
}
