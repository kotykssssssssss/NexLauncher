using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace NexLauncher.Services.Diagnostics;

/// <summary>Reads only literal mod IDs in [[mods]], never evaluates TOML expressions or code.</summary>
public static partial class ModDescriptors
{
    public static IEnumerable<string> TomlIds(string text)
    {
        var inMods = false;
        foreach (var source in text.Split('\n'))
        {
            var line = source.Trim();
            if (line.StartsWith('[')) { inMods = line == "[[mods]]" || line.StartsWith("[[mods]] #", StringComparison.Ordinal); continue; }
            if (!inMods) continue;
            var match = ModId().Match(line);
            if (match.Success) yield return match.Groups[1].Value;
        }
    }
    [GeneratedRegex("^modId\\s*=\\s*[\"']([a-zA-Z0-9_.-]{1,128})[\"']\\s*(?:#.*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ModId();
}
