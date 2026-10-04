using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;

namespace NexLauncher.Services.QuickCss;

/// <summary>Creates only typed, whitelisted style setters; no arbitrary XAML, selectors or bindings.</summary>
public sealed class QuickCssStyleApplier : IDisposable
{
    private readonly Window _window;
    private Styles? _overlay;
    private string? _activationClass;
    private List<Bitmap> _images = new();
    private readonly Dictionary<string, (bool Existed, object? Value)> _savedResources = new();
    private IResourceDictionary? _accentResources;
    private bool _disposed;

    public QuickCssStyleApplier(Window window) => _window = window;
    public bool IsApplied => _overlay is not null;

    internal QuickCssResult Apply(PreparedQuickCss prepared)
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var activationClass = "qc-theme-" + Guid.NewGuid().ToString("N");
        var (styles, diagnostics, accent, ruleCount) = BuildStyles(prepared, activationClass, inspectOnly: false);
        // Attach the replacement before releasing previous bitmaps. Failure preserves the old overlay.
        try { _window.Styles.Add(styles); }
        catch { _window.Styles.Remove(styles); throw; }
        // Fluent template children can retain cached style instances after a parent Styles.Remove.
        // A unique activator explicitly deactivates their setters and is never reused on reload.
        if (_activationClass is not null) _window.Classes.Remove(_activationClass);
        if (_overlay is not null) _window.Styles.Remove(_overlay);
        _activationClass = activationClass;
        _window.Classes.Add(activationClass);
        _overlay = styles;
        foreach (var image in _images) image.Dispose();
        _images = prepared.TakeImages();
        SetAccent(accent);
        return new(true, ruleCount, diagnostics.Distinct().ToArray(),
            diagnostics.Count == 0 ? $"Quick CSS применён: {ruleCount} правил." : $"Quick CSS применён: {ruleCount} правил. Есть замечания.");
    }

    /// <summary>Inspect parser/selector/value diagnostics without applying styles or reading image files.</summary>
    public static IReadOnlyList<QuickCssDiagnostic> Inspect(QuickCssDocument document)
    {
        Dispatcher.UIThread.VerifyAccess();
        using var prepared = new PreparedQuickCss(document, new Dictionary<string, Bitmap>(), document.Diagnostics);
        return BuildStyles(prepared, "", inspectOnly: true).Diagnostics.Distinct().OrderBy(x => x.Line).ToArray();
    }

    private static (Styles Styles, List<QuickCssDiagnostic> Diagnostics, Color? Accent, int RuleCount) BuildStyles(PreparedQuickCss prepared, string activationClass, bool inspectOnly)
    {
        var diagnostics = new List<QuickCssDiagnostic>(prepared.Diagnostics);
        var styles = new Styles();
        Color? accent = null;
        var ruleCount = 0;
        foreach (var rule in prepared.Document.Rules)
        {
            var used = false;
            foreach (var cssSelector in rule.Selectors)
            {
                if (!QuickCssSelectorCatalog.TryResolve(cssSelector, out var target))
                {
                    diagnostics.Add(new(rule.Line, $"Селектор «{cssSelector}» не поддерживается."));
                    continue;
                }
                var setters = new Dictionary<AvaloniaProperty, object?>();
                var adapterSetters = new List<KeyValuePair<string, object?>>();
                foreach (var declaration in rule.Declarations)
                {
                    var property = declaration.Property;
                    if (property.StartsWith("--", StringComparison.Ordinal)) continue;
                    if (property == "accent-color")
                    {
                        if (target.IsRoot && CssValueParser.TryColor(declaration.Value, out var parsed))
                        {
                            accent = ToColor(parsed);
                            used = true;
                        }
                        else diagnostics.Add(new(declaration.Line, "accent-color принимает цвет только для :root, window или #app."));
                        continue;
                    }
                    if (property is "background-size" or "background-position" or "background-opacity")
                    {
                        if (!rule.Declarations.Any(x => x.Property == "background-image"))
                            diagnostics.Add(new(declaration.Line, $"{property} требует background-image в том же правиле."));
                        continue;
                    }
                    if (!TryProperty(target.ControlType, property, out var avaloniaProperty))
                    {
                        diagnostics.Add(new(declaration.Line, $"Свойство «{property}» не поддерживается для «{cssSelector}»."));
                        continue;
                    }
                    if (!TryValue(declaration, rule, prepared, diagnostics, out var value)) continue;
                    setters[avaloniaProperty!] = value;
                    adapterSetters.Add(new(property, value));
                    used = true;
                }
                if (setters.Count > 0 && !inspectOnly)
                {
                    var style = new Style(s => target.Build(s, activationClass));
                    foreach (var setter in setters) style.Setters.Add(new Setter(setter.Key, setter.Value));
                    styles.Add(style);
                    AddTemplateAdapter(styles, target, adapterSetters, activationClass);
                }
            }
            if (used) ruleCount++;
        }
        return (styles, diagnostics, accent, ruleCount);
    }

    public void Clear()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_activationClass is not null) _window.Classes.Remove(_activationClass);
        _activationClass = null;
        if (_overlay is not null) _window.Styles.Remove(_overlay);
        _overlay = null;
        foreach (var image in _images) image.Dispose();
        _images.Clear();
        SetAccent(null);
    }

    private void SetAccent(Color? color)
    {
        if (color is null)
        {
            if (_accentResources is not null)
                foreach (var saved in _savedResources)
                {
                    if (saved.Value.Existed) _accentResources[saved.Key] = saved.Value.Value!;
                    else _accentResources.Remove(saved.Key);
                }
            _savedResources.Clear();
            _accentResources = null;
            return;
        }
        _accentResources ??= Application.Current?.Resources ?? _window.Resources;
        foreach (var key in new[] { "NxAccentColor", "NxAccentBrush" })
            if (!_savedResources.ContainsKey(key))
                _savedResources[key] = (_accentResources.TryGetValue(key, out var original), original);
        _accentResources["NxAccentColor"] = color.Value;
        _accentResources["NxAccentBrush"] = new SolidColorBrush(color.Value);
    }

    private static bool TryProperty(Type type, string name, out AvaloniaProperty? property)
    {
        var border = type == typeof(Border);
        var text = type == typeof(TextBlock);
        var panel = typeof(Panel).IsAssignableFrom(type);
        var templated = typeof(TemplatedControl).IsAssignableFrom(type);
        property = name switch
        {
            "background" or "background-color" or "background-image" => border ? Border.BackgroundProperty :
                panel ? Panel.BackgroundProperty : text ? TextBlock.BackgroundProperty : templated ? TemplatedControl.BackgroundProperty : null,
            "color" => TextElement.ForegroundProperty,
            "font-family" => TextElement.FontFamilyProperty,
            "font-size" => TextElement.FontSizeProperty,
            "font-weight" => TextElement.FontWeightProperty,
            "opacity" => Visual.OpacityProperty,
            "margin" => Layoutable.MarginProperty,
            "border-color" => border ? Border.BorderBrushProperty : templated ? TemplatedControl.BorderBrushProperty : null,
            "border-width" => border ? Border.BorderThicknessProperty : templated ? TemplatedControl.BorderThicknessProperty : null,
            "border-radius" => border ? Border.CornerRadiusProperty : templated ? TemplatedControl.CornerRadiusProperty : null,
            "padding" => border ? Border.PaddingProperty : text ? TextBlock.PaddingProperty : templated ? TemplatedControl.PaddingProperty : null,
            _ => null
        };
        return property is not null;
    }

    private static bool TryValue(QuickCssDeclaration declaration, QuickCssRule rule, PreparedQuickCss prepared,
        List<QuickCssDiagnostic> diagnostics, out object? value)
    {
        value = null;
        var input = declaration.Value;
        switch (declaration.Property)
        {
            case "background": case "background-color": case "color": case "border-color":
                if (CssValueParser.TryColor(input, out var color)) { value = new SolidColorBrush(ToColor(color)); return true; }
                break;
            case "font-size":
                if (CssValueParser.TryNumber(input, 8, 96, out var size)) { value = size; return true; }
                break;
            case "opacity":
                if (CssValueParser.TryNumber(input, 0, 1, out var opacity)) { value = opacity; return true; }
                break;
            case "border-width": case "padding": case "margin":
                if (CssValueParser.TryBox(input, declaration.Property == "border-width" ? 20 : 200, out var box))
                { value = new Thickness(box.Left, box.Top, box.Right, box.Bottom); return true; }
                break;
            case "border-radius":
                if (CssValueParser.TryBox(input, 100, out var corners))
                { value = new CornerRadius(corners.Top, corners.Right, corners.Bottom, corners.Left); return true; }
                break;
            case "font-weight":
                if (CssValueParser.TryFontWeight(input, out var weight)) { value = (FontWeight)weight; return true; }
                break;
            case "font-family":
                var family = Unquote(input.Trim());
                if (family.Length is > 0 and <= 100 && family.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_') &&
                    FontManager.Current.SystemFonts.Any(x => string.Equals(x.Name, family, StringComparison.OrdinalIgnoreCase)))
                { value = new FontFamily(family); return true; }
                diagnostics.Add(new(declaration.Line, "font-family принимает одно имя установленного шрифта, без URI или списка fallback."));
                return false;
            case "background-image":
                if (input.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)) { value = Brushes.Transparent; return true; }
                if (!prepared.Images.TryGetValue(input, out var bitmap)) return false; // Preparation already supplied the error.
                try
                {
                    var brush = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center };
                    foreach (var option in rule.Declarations)
                    {
                        var token = option.Value.Trim().ToLowerInvariant();
                        switch (option.Property)
                        {
                            case "background-size":
                                if (token is "cover" or "contain" or "stretch")
                                    brush.Stretch = token == "cover" ? Stretch.UniformToFill : token == "contain" ? Stretch.Uniform : Stretch.Fill;
                                else diagnostics.Add(new(option.Line, "background-size: доступны cover, contain и stretch."));
                                break;
                            case "background-position":
                                if (token is "center" or "top" or "bottom" or "left" or "right")
                                {
                                    brush.AlignmentX = token == "left" ? AlignmentX.Left : token == "right" ? AlignmentX.Right : AlignmentX.Center;
                                    brush.AlignmentY = token == "top" ? AlignmentY.Top : token == "bottom" ? AlignmentY.Bottom : AlignmentY.Center;
                                }
                                else diagnostics.Add(new(option.Line, "background-position: доступны center, top, bottom, left, right."));
                                break;
                            case "background-opacity":
                                if (CssValueParser.TryNumber(option.Value, 0, 1, out var alpha)) brush.Opacity = alpha;
                                else diagnostics.Add(new(option.Line, "background-opacity должна быть от 0 до 1."));
                                break;
                        }
                    }
                    value = brush;
                    return true;
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or NotSupportedException)
                { diagnostics.Add(new(declaration.Line, "Не удалось декодировать фоновое изображение.")); return false; }
        }
        diagnostics.Add(new(declaration.Line, $"Некорректное значение «{input}» у свойства «{declaration.Property}»."));
        return false;
    }

    private static void AddTemplateAdapter(Styles styles, QuickCssSelectorTarget target, IEnumerable<KeyValuePair<string, object?>> values, string activationClass)
    {
        var button = target.ControlType == typeof(Button) || target.ControlType == typeof(ListBoxItem);
        if (!button && target.ControlType != typeof(TextBox) && target.ControlType != typeof(ComboBox)) return;
        var adapter = new Style(s => button
            ? target.Build(s, activationClass).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter")
            : target.Build(s, activationClass).Template().OfType<Border>().Name(target.ControlType == typeof(TextBox) ? "PART_BorderElement" : "Background"));
        var effectiveSetters = new Dictionary<AvaloniaProperty, object?>();
        foreach (var pair in values)
        {
            AvaloniaProperty? property = pair.Key switch
            {
                "background" or "background-color" or "background-image" => button ? ContentPresenter.BackgroundProperty : Border.BackgroundProperty,
                "color" when button => ContentPresenter.ForegroundProperty,
                "border-color" => button ? ContentPresenter.BorderBrushProperty : Border.BorderBrushProperty,
                "border-width" => button ? ContentPresenter.BorderThicknessProperty : Border.BorderThicknessProperty,
                "border-radius" => button ? ContentPresenter.CornerRadiusProperty : Border.CornerRadiusProperty,
                _ => null
            };
            if (property is not null) effectiveSetters[property] = pair.Value;
        }
        foreach (var setter in effectiveSetters) adapter.Setters.Add(new Setter(setter.Key, setter.Value));
        if (adapter.Setters.Count > 0) styles.Add(adapter);
        if (target.ControlType == typeof(ComboBox))
        {
            var foreground = values.LastOrDefault(x => x.Key == "color");
            if (foreground.Key is not null)
            {
                // Fluent sets these foregrounds on template parts in focused/disabled states.
                var content = new Style(s => target.Build(s, activationClass).Template().OfType<ContentControl>().Name("ContentPresenter"));
                content.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, foreground.Value));
                styles.Add(content);
                var placeholder = new Style(s => target.Build(s, activationClass).Template().OfType<TextBlock>().Name("PlaceholderTextBlock"));
                placeholder.Setters.Add(new Setter(TextBlock.ForegroundProperty, foreground.Value));
                styles.Add(placeholder);
                var glyph = new Style(s => target.Build(s, activationClass).Template().OfType<PathIcon>().Name("DropDownGlyph"));
                glyph.Setters.Add(new Setter(PathIcon.ForegroundProperty, foreground.Value));
                styles.Add(glyph);
            }
        }
    }

    private static Color ToColor(CssColor color) => Color.FromArgb(color.A, color.R, color.G, color.B);
    internal static string Unquote(string value) => value.Length >= 2 && (value[0] == '\'' && value[^1] == '\'' || value[0] == '"' && value[^1] == '"') ? value[1..^1] : value;
    public void Dispose() { if (_disposed) return; Clear(); _disposed = true; }
}

