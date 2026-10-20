using System;
using System.Collections.Generic;
using System.Linq;

namespace NexLauncher.Services.QuickCss;

/// <summary>Edits parser-identified root declarations; never performs textual replacement in comments/strings.</summary>
public static class QuickCssRootTokens
{
    public static string Rewrite(string source, IReadOnlyDictionary<string, string> values)
    {
        var declarations = QuickCssParser.Parse(source).RootDeclarations ?? [];
        var missing = new Dictionary<string, string>(values, StringComparer.Ordinal);
        foreach (var declaration in declarations.Where(x => values.ContainsKey(x.Property)).OrderByDescending(x => x.Offset))
        {
            var next = declaration.Offset + declaration.Length;
            while (next < source.Length && char.IsWhiteSpace(source[next])) next++;
            var separator = next < source.Length && source[next] == ';' ? "" : ";";
            source = source.Remove(declaration.Offset, declaration.Length)
                .Insert(declaration.Offset, declaration.Property + ": " + values[declaration.Property] + separator);
            missing.Remove(declaration.Property);
        }
        if (missing.Count == 0) return source;
        var newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return source + newline + ":root {" + newline +
            string.Join(newline, missing.Select(x => "    " + x.Key + ": " + x.Value + ";")) + newline + "}" + newline;
    }
}
