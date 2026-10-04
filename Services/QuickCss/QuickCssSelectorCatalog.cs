using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Styling;

namespace NexLauncher.Services.QuickCss;

/// <summary>Public theme selectors map to stable semantic classes, never to user-supplied XAML.</summary>
public static class QuickCssSelectorCatalog
{
    private static readonly Dictionary<string, QuickCssSelectorTarget> Targets = new(StringComparer.Ordinal)
    {
        [":root"] = new(typeof(Window), "qc-app", IsRoot: true),
        ["window"] = new(typeof(Window), "qc-app", IsRoot: true),
        ["#app"] = new(typeof(Window), "qc-app", IsRoot: true),
        ["button"] = new(typeof(Button)),
        ["textbox"] = new(typeof(TextBox)),
        [".input"] = new(typeof(TextBox)),
        ["combobox"] = new(typeof(ComboBox)),
        ["text"] = new(typeof(TextBlock)),
        ["list-item"] = new(typeof(ListBoxItem)),
        [".card"] = new(typeof(Border), "card"),
        [".primary"] = new(typeof(Button), "primary"),
        [".button-primary"] = new(typeof(Button), "primary"),
        [".nav"] = new(typeof(Button), "nav"),
        [".navigation-item"] = new(typeof(Button), "nav"),
        [".nav.active"] = new(typeof(Button), "nav", "active"),
        [".nav.selected"] = new(typeof(Button), "nav", "active"),
        [".navigation-item.selected"] = new(typeof(Button), "nav", "active"),
        [".title"] = new(typeof(TextBlock), "title"),
        [".muted"] = new(typeof(TextBlock), "muted"),
        [".text-muted"] = new(typeof(TextBlock), "muted"),
        [".caption"] = new(typeof(TextBlock), "caption"),
        [".quiet"] = new(typeof(Button), "quiet"),
        ["#sidebar"] = new(typeof(Border), "qc-sidebar"),
        ["#content"] = new(typeof(Grid), "qc-content"),
        ["#play-button"] = new(typeof(Button), "qc-play-button"),
        ["#instance-card"] = new(typeof(Border), "qc-instance-card"),
        ["#account-panel"] = new(typeof(Border), "qc-account-panel"),
        ["#skin-panel"] = new(typeof(Border), "qc-skin-panel"),
        ["#log-panel"] = new(typeof(Border), "qc-log-panel"),
        ["#modrinth"] = new(typeof(Grid), "qc-modrinth"),
        ["#modrinth-filters"] = new(typeof(Border), "qc-modrinth-filters"),
        [".project-card"] = new(typeof(Button), "qc-project-card"),
        ["#project-header"] = new(typeof(Border), "qc-project-header"),
        ["#project-description"] = new(typeof(Border), "qc-project-description")
    };

    public static IEnumerable<string> Selectors => Targets.Keys;

    public static bool TryResolve(string selector, out QuickCssSelectorTarget target)
    {
        if (Targets.TryGetValue(selector, out target!)) return true;
        var index = selector.LastIndexOf(':');
        if (index < 1 || !Targets.TryGetValue(selector[..index], out var basic)) return false;
        var state = selector[index..];
        if (state == ":selected" && basic.ClassName == "nav")
        {
            target = basic with { ExtraClass = "active" };
            return true;
        }
        if (state == ":selected" && basic.ControlType == typeof(ListBoxItem))
        {
            target = basic with { State = ":selected" };
            return true;
        }
        if (state is not (":hover" or ":disabled") ||
            (basic.ControlType != typeof(Button) && basic.ControlType != typeof(TextBox) && basic.ControlType != typeof(ComboBox) && basic.ControlType != typeof(ListBoxItem)))
            return false;
        target = basic with { State = state == ":hover" ? ":pointerover" : ":disabled" };
        return true;
    }
}

public sealed record QuickCssSelectorTarget(Type ControlType, string? ClassName = null,
    string? ExtraClass = null, string? State = null, bool IsRoot = false)
{
    internal Selector Build(Selector? previous, string activationClass)
    {
        // All rules share an ancestor class activator, so active rules use one style priority.
        // CSS source order can then win across semantic classes and ordinary type selectors.
        var selector = IsRoot ? previous.OfType(ControlType).Class(activationClass) :
            previous.OfType<Window>().Class("qc-app").Class(activationClass).Descendant().OfType(ControlType);
        if (ClassName is not null) selector = selector.Class(ClassName);
        if (ExtraClass is not null) selector = selector.Class(ExtraClass);
        if (State is not null) selector = selector.Class(State);
        return selector;
    }
}

