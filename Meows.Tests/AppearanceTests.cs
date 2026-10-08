using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Meows.Services;
using Meows.ViewModels;

namespace Meows.Tests;

/// <summary>
/// The accent: known colours back as hex, anything else as the default, mixes that blend, and
/// applying with no application running done quietly.
/// </summary>
public sealed class AppearanceTests
{
    [Fact]
    public void Known_colours_come_back_as_hex_and_anything_else_is_the_default()
    {
        Assert.Equal("#0078D4", Appearance.NormaliseAccent("#0078D4"));
        Assert.Equal("#0078D4", Appearance.NormaliseAccent("0078d4"));
        Assert.Equal("#0078D4", Appearance.NormaliseAccent("  #0078D4  "));
        Assert.Equal("default", Appearance.NormaliseAccent(null));
        Assert.Equal("default", Appearance.NormaliseAccent(""));
        Assert.Equal("default", Appearance.NormaliseAccent("mauve"));
        Assert.Equal("default", Appearance.NormaliseAccent("#123456"));

        // Every choice the tab offers normalises to itself.
        Assert.All(new[] { "default", "#0078D4", "#107C10", "#CA5010", "#C239B3", "#8764B8", "#038387" },
            hex => Assert.Equal(hex, Appearance.NormaliseAccent(hex)));
    }

    [Fact]
    public void Mixing_blends_from_one_colour_to_the_other()
    {
        var black = Color.FromUInt32(0xFF000000);
        var white = Color.FromUInt32(0xFFFFFFFF);

        Assert.Equal(black, Appearance.Mix(black, white, 0));
        Assert.Equal(white, Appearance.Mix(black, white, 1));
        Assert.Equal(Color.FromUInt32(0xFF808080), Appearance.Mix(black, white, 0.5));
        Assert.Equal(black, Appearance.Mix(black, white, -2));
        Assert.Equal(white, Appearance.Mix(black, white, 2));
    }

    [Fact]
    public void Applying_with_no_application_is_quiet_and_names_its_keys()
    {
        Appearance.ApplyAccent("#0078D4");
        Appearance.ApplyAccent("default");
        Appearance.ApplyAccent("mauve");

        Assert.Equal(
            ["SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3", "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3"],
            Appearance.AccentKeys());
    }

    [Fact]
    public void Every_preset_has_valid_contrasting_light_and_dark_colours()
    {
        Assert.Equal(7, Appearance.Schemes.Count);
        foreach (var (id, _) in Appearance.Schemes.Where(s => s.Id != Appearance.CustomScheme))
        {
            foreach (var dark in new[] { false, true })
            {
                var colours = Appearance.Preset(id, dark);
                foreach (var slot in Appearance.ColourSlots)
                    Assert.True(Appearance.TryHex(Appearance.Colour(colours, slot), out _), $"{id} {dark} {slot}");
                Assert.True(Appearance.LowContrast(colours).Count == 0,
                    $"{id} {(dark ? "dark" : "light")}: {string.Join(", ", Appearance.LowContrast(colours))}");
            }
        }
    }

    [Fact]
    public void Custom_colours_are_variant_specific_and_invalid_settings_fall_back()
    {
        var original = Appearance.Preset(Appearance.OriginalScheme, false);
        var preferences = new ShellPreferences
        {
            ColourScheme = Appearance.CustomScheme,
            CustomLight = Appearance.WithColour(original, "card", "#123456"),
            CustomDark = Appearance.WithColour(Appearance.Preset(Appearance.OriginalScheme, true), "card", "#654321"),
        };
        Assert.Equal("#123456", Appearance.Resolve(preferences, false).Card);
        Assert.Equal("#654321", Appearance.Resolve(preferences, true).Card);

        preferences.CustomLight = preferences.CustomLight with { Card = "invalid" };
        Assert.Equal(original.Card, Appearance.Resolve(preferences, false).Card);
        preferences.ColourScheme = "no-such-scheme";
        Assert.Equal(original.Background, Appearance.Resolve(preferences, false).Background);
        Assert.Equal(Appearance.OriginalScheme, Appearance.NormaliseScheme(preferences.ColourScheme));
    }

    [Fact]
    public void Hex_validation_and_low_contrast_warnings_leave_invalid_input_alone()
    {
        Assert.True(Appearance.TryHex("  12ab34  ", out var hex));
        Assert.Equal("#12AB34", hex);
        Assert.False(Appearance.TryHex("#12345", out _));
        Assert.False(Appearance.TryHex("#GGGGGG", out _));
        Assert.False(Appearance.TryHex("#FF112233", out _));

        var colours = Appearance.Preset("classic", false) with { Foreground = "#ECE9E0" };
        Assert.Contains("card", Appearance.LowContrast(colours));
    }

    [AvaloniaFact]
    public void A_scheme_updates_resource_brushes_when_the_theme_changes_and_original_restores_them()
    {
        var app = Application.Current!;
        var preferences = new ShellPreferences { Theme = Appearance.Light, ColourScheme = "classic" };
        var window = new Window { Width = 400, Height = 300, Content = new Border() };
        var before = app.RequestedThemeVariant;
        try
        {
            app.RequestedThemeVariant = ThemeVariant.Light;
            Appearance.ApplyScheme(preferences);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.TryFindResource("MeowsCard", window.ActualThemeVariant, out var light));
            Assert.Equal(Color.Parse(Appearance.Preset("classic", false).Card), Assert.IsType<SolidColorBrush>(light).Color);

            app.RequestedThemeVariant = ThemeVariant.Dark;
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.TryFindResource("MeowsCard", window.ActualThemeVariant, out var dark));
            Assert.Equal(Color.Parse(Appearance.Preset("classic", true).Card), Assert.IsType<SolidColorBrush>(dark).Color);

            preferences.ColourScheme = Appearance.OriginalScheme;
            Appearance.ApplyScheme(preferences);
            Assert.True(window.TryFindResource("MeowsCard", window.ActualThemeVariant, out var restored));
            Assert.Equal(Color.Parse("#1C1C22"), Assert.IsType<SolidColorBrush>(restored).Color);
        }
        finally
        {
            window.Close();
            Appearance.ApplyScheme(new ShellPreferences());
            app.RequestedThemeVariant = before;
            Dispatcher.UIThread.RunJobs();
        }
    }

    [Fact]
    public void Customizing_saves_valid_changes_preserves_the_other_variant_and_resets_in_one_click()
    {
        var root = Path.Combine(Path.GetTempPath(), "meows-appearance-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new ShellSettings(root, Path.Combine(root, "none"));
            var preferences = settings.LoadPreferences();
            preferences.Theme = Appearance.Light;
            var model = new SettingsViewModel(settings, TestStrings.Load(), new ShellLog(), preferences);
            model.SelectedScheme = model.Schemes.Single(s => s.Id == "paper");
            Assert.Equal("paper", settings.LoadPreferences().ColourScheme);
            model.CustomizeCommand.Execute(null);
            Assert.True(model.IsCustomScheme);
            Assert.Equal(Appearance.Preset("paper", true).Card, preferences.CustomDark?.Card);
            var card = model.Colours.Single(row => row.Slot == "card");
            var was = preferences.CustomLight!.Card;

            card.Hex = "not a colour";
            Assert.True(card.IsInvalid);
            Assert.Equal(was, settings.LoadPreferences().CustomLight?.Card);
            card.Hex = "#ABCDEF";
            Assert.False(card.IsInvalid);
            Assert.Equal("#ABCDEF", settings.LoadPreferences().CustomLight?.Card);
            Assert.Equal("#ABCDEF", model.Preview.Colours.Card);
            Assert.Equal(Appearance.Preset("paper", true).Card, preferences.CustomDark?.Card);

            model.EditDark = true;
            Assert.Equal(Appearance.Preset("paper", true).Card, model.Preview.Colours.Card);
            model.ResetAppearanceCommand.Execute(null);
            var reset = settings.LoadPreferences();
            Assert.Equal(Appearance.OriginalScheme, reset.ColourScheme);
            Assert.Equal(Appearance.DefaultAccent, reset.Accent);
            Assert.Equal(Appearance.System, reset.Theme);
            Assert.Null(reset.CustomLight);
            Assert.Null(reset.CustomDark);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
