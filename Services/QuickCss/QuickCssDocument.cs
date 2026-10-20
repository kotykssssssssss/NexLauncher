using System.Collections.Generic;
using NexLauncher.Models;

namespace NexLauncher.Services.QuickCss;

public sealed record QuickCssDeclaration(string Property, string Value, int Line, int Offset = 0, int Length = 0);
public sealed record QuickCssRule(IReadOnlyList<string> Selectors, IReadOnlyList<QuickCssDeclaration> Declarations, int Line);
public sealed record QuickCssDiagnostic(int Line, string Message);
public sealed record QuickCssDocument(IReadOnlyList<QuickCssRule> Rules, IReadOnlyList<QuickCssDiagnostic> Diagnostics,
    HomeLayout? HomeLayout = null, IReadOnlyList<QuickCssDeclaration>? LayoutDeclarations = null,
    IReadOnlyDictionary<string, string>? Appearance = null, IReadOnlyList<QuickCssDeclaration>? RootDeclarations = null);

/// <summary>CSS channel order, including alpha LAST in hexadecimal input.</summary>
public readonly record struct CssColor(byte R, byte G, byte B, byte A = 255);
public readonly record struct CssBox(double Top, double Right, double Bottom, double Left);
