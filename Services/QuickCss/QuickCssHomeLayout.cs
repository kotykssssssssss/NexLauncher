using System;
using System.Collections.Generic;
using NexLauncher.Models;

namespace NexLauncher.Services.QuickCss;

/// <summary>A reserved launcher token, independent of Avalonia styling and installation logic.</summary>
public static class QuickCssHomeLayout
{
    public static string Token(HomeLayout layout) => layout switch
    {
        HomeLayout.Default => "default", HomeLayout.Tiles => "tiles", HomeLayout.List => "list",
        _ => throw new ArgumentOutOfRangeException(nameof(layout))
    };
    public static bool TryParse(string value, out HomeLayout layout)
    {
        layout = value switch { "tiles" => HomeLayout.Tiles, "list" => HomeLayout.List, _ => HomeLayout.Default };
        return value is "default" or "tiles" or "list";
    }
    public static string Rewrite(string source, HomeLayout layout) => QuickCssRootTokens.Rewrite(source,
        new Dictionary<string, string> { ["--home-layout"] = Token(layout) });
}
