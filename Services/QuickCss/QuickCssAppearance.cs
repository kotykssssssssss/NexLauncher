using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace NexLauncher.Services.QuickCss;

public sealed record AppearanceOption(string Token, string Label, string DefaultValue, string Hint,
    bool IsColor = true, double Minimum = 0, double Maximum = 0);

/// <summary>The same bounded, typed appearance vocabulary is used by Settings and Quick CSS.</summary>
public static class QuickCssAppearance
{
    public static IReadOnlyList<AppearanceOption> Options { get; } = Array.AsReadOnly(new[]
    {
        new AppearanceOption("--nx-accent", "Акцент", "#78D9B3", "Цвет основных кнопок и ссылок"),
        new AppearanceOption("--nx-background", "Фон окна", "#181A1D", "Основной фон"),
        new AppearanceOption("--nx-sidebar", "Боковая панель", "#202225", "Фон навигации"),
        new AppearanceOption("--nx-surface", "Карточки", "#26292D", "Фон панелей и плиток"),
        new AppearanceOption("--nx-input", "Поля ввода", "#202327", "Фон текстовых полей и списков"),
        new AppearanceOption("--nx-border", "Границы", "#3B4148", "Цвет рамок"),
        new AppearanceOption("--nx-text", "Основной текст", "#EDF0F3", "Цвет текста"),
        new AppearanceOption("--nx-muted", "Вторичный текст", "#ABB3BC", "Подписи и пояснения"),
        new AppearanceOption("--nx-font-size", "Размер текста", "14px", "12–20 px · заголовки сохраняют свой размер", false, 12, 20),
        new AppearanceOption("--nx-radius", "Скругление", "12px", "0–24 px · карточки, кнопки, поля", false, 0, 24),
        new AppearanceOption("--nx-card-padding", "Отступы в карточках", "24px", "8–32 px", false, 8, 32),
        new AppearanceOption("--nx-content-gap", "Отступ от края окна", "32px", "12–40 px · рабочая область", false, 12, 40)
    });

    public static bool TryNormalize(AppearanceOption option, string value, out string normalized)
    {
        normalized = "";
        // Transparent foregrounds/backgrounds can make all recovery controls inaccessible.
        if (option.IsColor && CssValueParser.TryColor(value, out var color) && color.A == 255)
        { normalized = $"#{color.R:X2}{color.G:X2}{color.B:X2}"; return true; }
        if (!option.IsColor && CssValueParser.TryNumber(value, option.Minimum, option.Maximum, out var number))
        { normalized = number.ToString("0.##", CultureInfo.InvariantCulture) + "px"; return true; }
        return false;
    }

    public static Dictionary<string, string> ValidateAndCopy(IReadOnlyDictionary<string, string>? values)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (values is null) return result; // Older settings have no appearance section.
        if (values.Count > Options.Count) throw new InvalidDataException("Слишком много параметров оформления.");
        foreach (var (key, value) in values)
        {
            var option = Options.FirstOrDefault(x => x.Token == key);
            if (option is null || !TryNormalize(option, value, out var normalized))
                throw new InvalidDataException($"Недопустимый параметр оформления «{key}».");
            result.Add(key, normalized);
        }
        return result;
    }

    public static IReadOnlyList<QuickCssRule> Rules(IReadOnlyDictionary<string, string>? values)
    {
        var rules = new List<QuickCssRule>();
        if (values is null) return rules;
        foreach (var option in Options)
        {
            if (!values.TryGetValue(option.Token, out var value)) continue;
            void Rule(string selector, string property) => rules.Add(new([selector], [new(property, value, 0)], 0));
            switch (option.Token)
            {
                case "--nx-accent":
                    Rule(":root", "accent-color");
                    Rule(".primary", "background-color"); Rule(".nav.active", "color");
                    if (CssValueParser.TryColor(value, out var accent))
                    {
                        static string Mix(CssColor c, byte target, double amount) => $"#{(byte)(c.R * (1 - amount) + target * amount):X2}{(byte)(c.G * (1 - amount) + target * amount):X2}{(byte)(c.B * (1 - amount) + target * amount):X2}";
                        var foreground = .2126 * accent.R + .7152 * accent.G + .0722 * accent.B < 145 ? "#FFFFFF" : "#101820";
                        rules.Add(new([".primary", ".primary:hover"], [new("color", foreground, 0)], 0));
                        rules.Add(new([".primary:hover"], [new("background-color", Mix(accent, 255, .16), 0)], 0));
                        rules.Add(new([".primary:disabled"], [new("background-color", Mix(accent, 24, .7), 0), new("color", Mix(accent, 255, .6), 0)], 0));
                        rules.Add(new([".nav.active"], [new("background-color", Mix(accent, 24, .8), 0)], 0));
                    }
                    break;
                case "--nx-background": Rule(":root", "background-color"); break;
                case "--nx-sidebar": Rule("#sidebar", "background-color"); break;
                case "--nx-surface": Rule(".card", "background-color"); Rule(".home-tile", "background-color"); break;
                case "--nx-input": Rule("textbox", "background-color"); Rule("combobox", "background-color"); break;
                case "--nx-border": foreach (var s in new[] { ".card", "#sidebar", "textbox", "combobox" }) Rule(s, "border-color"); break;
                case "--nx-text": Rule(":root", "color"); Rule("text", "color"); Rule(".nav", "color"); break;
                case "--nx-muted": Rule(".muted", "color"); Rule(".caption", "color"); break;
                case "--nx-font-size": Rule(":root", "font-size"); break;
                case "--nx-radius": foreach (var s in new[] { ".card", ".home-tile", "button", "textbox", "combobox" }) Rule(s, "border-radius"); break;
                case "--nx-card-padding": Rule(".card", "padding"); break;
                case "--nx-content-gap": Rule("#content", "margin"); break;
            }
        }
        return rules;
    }
}
