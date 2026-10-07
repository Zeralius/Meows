using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace Meows.Services;

/// <summary>
/// Turns a saved theme choice into something Avalonia understands, and puts it on the running
/// application.
///
/// <c>ThemeVariant.Default</c> is not a third colour scheme: it means follow whatever Windows is
/// set to, and Avalonia keeps watching after that, so switching the desktop to light mode moves
/// the window with it.
///
/// The accent is the same idea one level down: the Fluent theme reads the SystemAccentColor
/// resources, so naming a colour repaints every control that uses the accent, in both themes,
/// without touching a single style. "default" clears the overrides and the theme is itself.
/// </summary>
public static class Appearance
{
    public const string System = "system";
    public const string Light = "light";
    public const string Dark = "dark";

    /// <summary>No accent of our own: the Fluent theme as it ships.</summary>
    public const string DefaultAccent = "default";

    /// <summary>The accents to pick from: what it is called, and the colour.</summary>
    public static readonly IReadOnlyList<(string Name, string Hex)> Accents =
    [
        ("blue", "#0078D4"),
        ("green", "#107C10"),
        ("amber", "#CA5010"),
        ("rose", "#C239B3"),
        ("violet", "#8764B8"),
        ("teal", "#038387"),
    ];

    public static ThemeVariant VariantFor(string choice) => choice?.ToLowerInvariant() switch
    {
        Light => ThemeVariant.Light,
        Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    /// <summary>Anything we do not recognise counts as following the system.</summary>
    public static string Normalise(string? choice) => choice?.ToLowerInvariant() switch
    {
        Light => Light,
        Dark => Dark,
        _ => System,
    };

    public static void Apply(string choice)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = VariantFor(choice);
    }

    /// <summary>
    /// A saved accent back to a hex we know, or "default". Hex travels in any case, with or
    /// without the hash; anything else is no accent.
    /// </summary>
    public static string NormaliseAccent(string? choice)
    {
        if (choice is { } named)
        {
            var hex = named.Trim().TrimStart('#').ToUpperInvariant();
            if (Accents.Any(a => a.Hex.TrimStart('#').Equals(hex, StringComparison.OrdinalIgnoreCase)))
                return "#" + hex;
        }
        return DefaultAccent;
    }

    /// <summary>
    /// Puts the accent on the running application, or takes ours back off. Nothing happens
    /// with no application, which is what a test gets.
    /// </summary>
    public static void ApplyAccent(string choice)
    {
        if (Application.Current is not { } app)
            return;

        var hex = NormaliseAccent(choice);
        if (hex == DefaultAccent)
        {
            foreach (var key in AccentKeys())
                app.Resources.Remove(key);
            return;
        }

        var base_ = Color.Parse(hex);
        app.Resources["SystemAccentColor"] = base_;
        app.Resources["SystemAccentColorLight1"] = Mix(base_, Color.FromUInt32(0xFFFFFFFF), 0.2);
        app.Resources["SystemAccentColorLight2"] = Mix(base_, Color.FromUInt32(0xFFFFFFFF), 0.4);
        app.Resources["SystemAccentColorLight3"] = Mix(base_, Color.FromUInt32(0xFFFFFFFF), 0.6);
        app.Resources["SystemAccentColorDark1"] = Mix(base_, Color.FromUInt32(0xFF000000), 0.2);
        app.Resources["SystemAccentColorDark2"] = Mix(base_, Color.FromUInt32(0xFF000000), 0.4);
        app.Resources["SystemAccentColorDark3"] = Mix(base_, Color.FromUInt32(0xFF000000), 0.6);
    }

    /// <summary>The resource keys an accent owns. Taking them back off restores the theme.</summary>
    public static IReadOnlyList<string> AccentKeys() =>
    [
        "SystemAccentColor",
        "SystemAccentColorLight1",
        "SystemAccentColorLight2",
        "SystemAccentColorLight3",
        "SystemAccentColorDark1",
        "SystemAccentColorDark2",
        "SystemAccentColorDark3",
    ];

    /// <summary>Mixes two colours: 0 is all of the first, 1 all of the second.</summary>
    public static Color Mix(Color first, Color second, double amount)
    {
        byte Blend(byte a, byte b) => (byte)Math.Round(a + (b - a) * Math.Clamp(amount, 0, 1));
        return new Color(Blend(first.A, second.A), Blend(first.R, second.R), Blend(first.G, second.G), Blend(first.B, second.B));
    }

    public const string OriginalScheme = "original";
    public const string CustomScheme = "custom";

    public static readonly IReadOnlyList<(string Id, string Label)> Schemes =
    [
        (OriginalScheme, "settings.scheme.original"),
        ("classic", "settings.scheme.classic"),
        ("amber", "settings.scheme.amber"),
        ("green", "settings.scheme.green"),
        ("midnight", "settings.scheme.midnight"),
        ("paper", "settings.scheme.paper"),
        (CustomScheme, "settings.scheme.custom"),
    ];

    public static readonly IReadOnlyList<string> ColourSlots =
    [
        "background", "panel", "card", "foreground", "border", "selection", "selectionText",
        "accent", "info", "infoText", "warning", "warningText", "danger", "dangerText",
    ];

    private static Application? _watchedApp;
    private static ShellPreferences? _watchedPreferences;

    public static string NormaliseScheme(string? scheme) =>
        Schemes.FirstOrDefault(s => string.Equals(s.Id, scheme, StringComparison.OrdinalIgnoreCase)).Id
        ?? OriginalScheme;

    public static bool TryHex(string? value, out string hex)
    {
        hex = "";
        if (value is null)
            return false;
        var digits = value.Trim().TrimStart('#');
        if (digits.Length != 6 || !digits.All(Uri.IsHexDigit))
            return false;
        hex = "#" + digits.ToUpperInvariant();
        return true;
    }

    public static string Colour(SchemeColours colours, string slot) => slot switch
    {
        "background" => colours.Background,
        "panel" => colours.Panel,
        "card" => colours.Card,
        "foreground" => colours.Foreground,
        "border" => colours.Border,
        "selection" => colours.Selection,
        "selectionText" => colours.SelectionText,
        "accent" => colours.Accent,
        "info" => colours.Info,
        "infoText" => colours.InfoText,
        "warning" => colours.Warning,
        "warningText" => colours.WarningText,
        "danger" => colours.Danger,
        "dangerText" => colours.DangerText,
        _ => throw new ArgumentOutOfRangeException(nameof(slot)),
    };

    public static SchemeColours WithColour(SchemeColours colours, string slot, string hex) => slot switch
    {
        "background" => colours with { Background = hex },
        "panel" => colours with { Panel = hex },
        "card" => colours with { Card = hex },
        "foreground" => colours with { Foreground = hex },
        "border" => colours with { Border = hex },
        "selection" => colours with { Selection = hex },
        "selectionText" => colours with { SelectionText = hex },
        "accent" => colours with { Accent = hex },
        "info" => colours with { Info = hex },
        "infoText" => colours with { InfoText = hex },
        "warning" => colours with { Warning = hex },
        "warningText" => colours with { WarningText = hex },
        "danger" => colours with { Danger = hex },
        "dangerText" => colours with { DangerText = hex },
        _ => throw new ArgumentOutOfRangeException(nameof(slot)),
    };

    public static SchemeColours Resolve(ShellPreferences preferences, bool dark)
    {
        var scheme = NormaliseScheme(preferences.ColourScheme);
        var original = Preset(OriginalScheme, dark);
        var selected = scheme == CustomScheme
            ? dark ? preferences.CustomDark ?? original : preferences.CustomLight ?? original
            : Preset(scheme, dark);
        foreach (var slot in ColourSlots)
        {
            if (!TryHex(Colour(selected, slot), out var hex))
                selected = WithColour(selected, slot, Colour(original, slot));
            else
                selected = WithColour(selected, slot, hex);
        }
        if (NormaliseAccent(preferences.Accent) is { } accent && accent != DefaultAccent)
            selected = selected with { Accent = accent };
        return selected;
    }

    public static SchemeColours Preset(string scheme, bool dark) => NormaliseScheme(scheme) switch
    {
        "classic" => dark
            ? Build(true, "#252629", "#323438", "#3E4044", "#F1F0EC", "#75777A", "#305BA4", "#FFFFFF", "#82A8EC")
            : Build(false, "#D4D0C8", "#C2BFB6", "#ECE9E0", "#191919", "#77736D", "#0A246A", "#FFFFFF", "#0A246A"),
        "amber" => dark
            ? Build(true, "#140F09", "#1D160E", "#271D12", "#F3D297", "#6B512C", "#705018", "#FFF4D6", "#ECAA34")
            : Build(false, "#FAF2DD", "#F0E4C8", "#FFF9E8", "#4C3512", "#B69B70", "#9A5D18", "#FFFFFF", "#A5661D"),
        "green" => dark
            ? Build(true, "#0D1710", "#132015", "#1B2A1D", "#CCEBC8", "#41634A", "#285E38", "#F2FFEF", "#70C873")
            : Build(false, "#ECF5E8", "#DCECD7", "#FBFFF9", "#203D25", "#8EB497", "#326D3C", "#FFFFFF", "#328243"),
        "midnight" => dark
            ? Build(true, "#101829", "#16213A", "#1F2D48", "#DFE8FC", "#465C81", "#32588F", "#F4F7FF", "#82AAFF")
            : Build(false, "#E8EDF9", "#DAE3F4", "#FFFFFF", "#25304C", "#A4B4D2", "#B9D0FA", "#122345", "#344FA0"),
        "paper" => dark
            ? Build(true, "#211C17", "#2A231C", "#352B21", "#F4E6CC", "#715B46", "#704F32", "#FFF0D3", "#C99D65")
            : Build(false, "#F7F1E1", "#EAE0CA", "#FFF9ED", "#413528", "#BAAA90", "#D2B98B", "#352514", "#916747"),
        _ => dark
            ? Build(true, "#17171C", "#141418", "#1C1C22", "#EAEAF0", "#2E2E36", "#24384A", "#FFFFFF", "#0078D4")
            : Build(false, "#F4F4F7", "#ECECF1", "#FFFFFF", "#202026", "#D2D2DC", "#CFE3F5", "#162938", "#0078D4"),
    };

    private static SchemeColours Build(bool dark, string background, string panel, string card,
        string foreground, string border, string selection, string selectionText, string accent)
    {
        var baseColour = Color.Parse(background);
        var accentColour = Color.Parse(accent);
        var warning = Color.Parse(dark ? "#DDBB67" : "#835308");
        var danger = Color.Parse(dark ? "#FFA9A9" : "#A32232");
        var infoSurface = MixHex(baseColour, accentColour, dark ? .18 : .13);
        var warningSurface = MixHex(baseColour, warning, dark ? .18 : .13);
        var dangerSurface = MixHex(baseColour, danger, dark ? .18 : .13);
        return new SchemeColours
        {
            Background = background, Panel = panel, Card = card, Foreground = foreground,
            Border = border, Selection = selection, SelectionText = selectionText, Accent = accent,
            Info = infoSurface,
            InfoText = EnsureContrast(MixHex(accentColour, Color.Parse(dark ? "#FFFFFF" : "#000000"), dark ? .33 : .27), infoSurface, dark),
            Warning = warningSurface,
            WarningText = EnsureContrast(Hex(warning), warningSurface, dark),
            Danger = dangerSurface,
            DangerText = EnsureContrast(Hex(danger), dangerSurface, dark),
        };
    }

    private static string EnsureContrast(string text, string surface, bool dark)
    {
        var end = Color.Parse(dark ? "#FFFFFF" : "#000000");
        var initial = Color.Parse(text);
        for (var step = 0; step <= 20; step++)
        {
            var candidate = MixHex(initial, end, step / 20d);
            if (Contrast(candidate, surface) >= 4.5)
                return candidate;
        }
        return Hex(end);
    }

    public static double Contrast(string first, string second)
    {
        static double Luminance(string hex)
        {
            var color = Color.Parse(hex);
            static double Channel(byte b)
            {
                var value = b / 255d;
                return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
            }
            return .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
        }
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    public static IReadOnlyList<string> LowContrast(SchemeColours colours)
    {
        var low = new List<string>();
        foreach (var (key, text, background) in new[]
        {
            ("foreground", colours.Foreground, colours.Background),
            ("card", colours.Foreground, colours.Card),
            ("selection", colours.SelectionText, colours.Selection),
            ("info", colours.InfoText, colours.Info),
            ("warning", colours.WarningText, colours.Warning),
            ("danger", colours.DangerText, colours.Danger),
        })
            if (Contrast(text, background) < 4.5)
                low.Add(key);
        return low;
    }

    private static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static string MixHex(Color first, Color second, double amount) => Hex(Mix(first, second, amount));

    public static void ApplyScheme(ShellPreferences preferences)
    {
        if (Application.Current is not { } app)
            return;
        _watchedPreferences = preferences;
        if (!ReferenceEquals(_watchedApp, app))
        {
            if (_watchedApp is not null)
                _watchedApp.ActualThemeVariantChanged -= ThemeChanged;
            _watchedApp = app;
            app.ActualThemeVariantChanged += ThemeChanged;
        }
        ApplyCurrent(app, preferences);
    }

    private static void ThemeChanged(object? sender, EventArgs e)
    {
        if (_watchedApp is { } app && _watchedPreferences is { } preferences)
            ApplyCurrent(app, preferences);
    }

    private static void ApplyCurrent(Application app, ShellPreferences preferences)
    {
        foreach (var key in PaletteKeys())
            app.Resources.Remove(key);
        var scheme = NormaliseScheme(preferences.ColourScheme);
        if (scheme != OriginalScheme)
        {
            var colours = Resolve(preferences, app.ActualThemeVariant == ThemeVariant.Dark);
            var background = Color.Parse(colours.Background);
            var panel = Color.Parse(colours.Panel);
            var card = Color.Parse(colours.Card);
            var foreground = Color.Parse(colours.Foreground);
            var border = Color.Parse(colours.Border);
            var selection = Color.Parse(colours.Selection);
            var accent = Color.Parse(colours.Accent);
            var info = Color.Parse(colours.Info);
            var warning = Color.Parse(colours.Warning);
            var danger = Color.Parse(colours.Danger);
            var brush = new Dictionary<string, Color>
            {
                ["MeowsSunken"] = Mix(background, Color.Parse(app.ActualThemeVariant == ThemeVariant.Dark ? "#000000" : "#FFFFFF"), .12),
                ["MeowsPanel"] = panel,
                ["MeowsBase"] = background,
                ["MeowsBar"] = Mix(panel, background, .35),
                ["MeowsCard"] = card,
                ["MeowsInset"] = Mix(card, background, .35),
                ["MeowsRaised"] = Mix(card, foreground, .07),
                ["MeowsHeader"] = Mix(panel, foreground, .08),
                ["MeowsForeground"] = foreground,
                ["MeowsLine"] = border,
                ["MeowsLineSoft"] = Mix(border, background, .55),
                ["MeowsSelection"] = selection,
                ["MeowsSelectionLine"] = Mix(accent, selection, .4),
                ["MeowsSelectionText"] = Color.Parse(colours.SelectionText),
                ["MeowsInfo"] = info,
                ["MeowsInfoLine"] = Mix(Color.Parse(colours.InfoText), info, .4),
                ["MeowsInfoText"] = Color.Parse(colours.InfoText),
                ["MeowsWarn"] = warning,
                ["MeowsWarnLine"] = Mix(Color.Parse(colours.WarningText), warning, .4),
                ["MeowsWarnText"] = Color.Parse(colours.WarningText),
                ["MeowsDanger"] = danger,
                ["MeowsDangerLine"] = Mix(Color.Parse(colours.DangerText), danger, .4),
                ["MeowsDangerText"] = Color.Parse(colours.DangerText),
                ["MeowsDangerStrong"] = Color.Parse(colours.DangerText),
                ["MeowsGoodText"] = Color.Parse(colours.InfoText),
                ["MeowsBadge"] = Color.FromArgb(0xE6, selection.R, selection.G, selection.B),
                ["MeowsDangerBadge"] = Color.FromArgb(0xCC, danger.R, danger.G, danger.B),
            };
            foreach (var (key, color) in brush)
                app.Resources[key] = new SolidColorBrush(color);
            var groups = new[] { "Slate", "Blue", "Green", "Amber", "Rose", "Violet", "Teal" };
            var tones = new[] { "#6B7280", "#5B8DB8", "#5E9E6E", "#B8925B", "#B8697A", "#8B7AB8", "#5BA8A0" };
            for (var i = 0; i < groups.Length; i++)
                app.Resources["MeowsGroup" + groups[i]] = new SolidColorBrush(Mix(Color.Parse(tones[i]), background, .25));
            app.Resources["SystemControlForegroundBaseHighBrush"] = new SolidColorBrush(foreground);
        }
        var overrideAccent = NormaliseAccent(preferences.Accent);
        var selectedAccent = scheme == OriginalScheme ? overrideAccent : Resolve(preferences, app.ActualThemeVariant == ThemeVariant.Dark).Accent;
        ApplyAccent(selectedAccent);
    }

    public static IReadOnlyList<string> PaletteKeys() =>
    [
        "MeowsSunken", "MeowsPanel", "MeowsBase", "MeowsBar", "MeowsCard", "MeowsInset",
        "MeowsRaised", "MeowsHeader", "MeowsForeground", "MeowsLine", "MeowsLineSoft",
        "MeowsSelection", "MeowsSelectionLine", "MeowsSelectionText", "MeowsInfo",
        "MeowsInfoLine", "MeowsInfoText", "MeowsWarn", "MeowsWarnLine", "MeowsWarnText",
        "MeowsDanger", "MeowsDangerLine", "MeowsDangerText", "MeowsDangerStrong",
        "MeowsGoodText", "MeowsBadge", "MeowsDangerBadge", "MeowsGroupSlate", "MeowsGroupBlue",
        "MeowsGroupGreen", "MeowsGroupAmber", "MeowsGroupRose", "MeowsGroupViolet",
        "MeowsGroupTeal", "SystemControlForegroundBaseHighBrush",
    ];
}

public sealed record SchemeColours
{
    public string Background { get; init; } = "#F4F4F7";
    public string Panel { get; init; } = "#ECECF1";
    public string Card { get; init; } = "#FFFFFF";
    public string Foreground { get; init; } = "#202026";
    public string Border { get; init; } = "#D2D2DC";
    public string Selection { get; init; } = "#CFE3F5";
    public string SelectionText { get; init; } = "#162938";
    public string Accent { get; init; } = "#0078D4";
    public string Info { get; init; } = "#E6F1FA";
    public string InfoText { get; init; } = "#1B5175";
    public string Warning { get; init; } = "#FBF1DC";
    public string WarningText { get; init; } = "#7A5310";
    public string Danger { get; init; } = "#FCE8E8";
    public string DangerText { get; init; } = "#8C1F27";
}
