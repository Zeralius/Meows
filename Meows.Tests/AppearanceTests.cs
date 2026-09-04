using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia.Styling;
using Meows.Services;

namespace Meows.Tests;

/// <summary>Turning a saved choice into a theme, and remembering it.</summary>
public class AppearanceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "meows-appearance-" + Guid.NewGuid().ToString("N"));

    private ShellSettings Settings() =>
        new(Path.Combine(_root, "Meows"), Path.Combine(_root, "no old folder"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Following_the_system_is_the_default()
    {
        var preferences = Settings().LoadPreferences();

        Assert.Equal(Appearance.System, preferences.Theme);
        Assert.Equal("system", preferences.Language);
    }

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    [InlineData("system")]
    public void A_theme_choice_survives_a_restart(string choice)
    {
        var settings = Settings();
        settings.SavePreferences(new ShellPreferences { Theme = choice, Language = "de" });

        // A second instance reads the file rather than the object we just wrote.
        var again = Settings().LoadPreferences();

        Assert.Equal(choice, again.Theme);
        Assert.Equal("de", again.Language);
    }

    [Fact]
    public void An_unreadable_preferences_file_is_set_aside_rather_than_overwritten()
    {
        var settings = Settings();
        var file = Path.Combine(settings.Root, "preferences.json");
        File.WriteAllText(file, "{ this is not json");

        var complaints = new List<string>();
        settings.Report = complaints.Add;

        // Defaults, so the window still opens.
        Assert.Equal(Appearance.System, settings.LoadPreferences().Theme);

        // The original is kept. Reading defaults and then writing them back over the top is how
        // one bad byte quietly becomes a lost setting.
        Assert.False(File.Exists(file));
        Assert.NotEmpty(Directory.GetFiles(settings.Root, "preferences.json.unreadable-*"));
        Assert.NotEmpty(complaints);
    }

    [Fact]
    public void Anything_unrecognised_counts_as_following_the_system()
    {
        Assert.Equal(Appearance.System, Appearance.Normalise("whatever"));
        Assert.Equal(Appearance.System, Appearance.Normalise(null));
        Assert.Equal(Appearance.Light, Appearance.Normalise("LIGHT"));
    }

    [Fact]
    public void Following_the_system_is_not_a_third_colour_scheme()
    {
        // Default is Avalonia's "ask the platform", which keeps following it afterwards, so the
        // window moves with Windows when it switches itself at sunset.
        Assert.Equal(ThemeVariant.Default, Appearance.VariantFor(Appearance.System));
        Assert.Equal(ThemeVariant.Light, Appearance.VariantFor(Appearance.Light));
        Assert.Equal(ThemeVariant.Dark, Appearance.VariantFor(Appearance.Dark));
    }
}

/// <summary>
/// The palette and the views that read it, checked against the sources rather than the build.
///
/// A colour token that does not exist resolves to nothing and paints the control transparent, and
/// a translation key that does not exist shows the key. Neither throws, neither fails a build, and
/// both look like something else went wrong.
/// </summary>
public class SourceTests
{
    /// <summary>
    /// Walks up from the test binaries to the checkout. Everything here reads the repository, so
    /// a wrong answer has to fail rather than quietly find nothing.
    /// </summary>
    private static DirectoryInfo Repository()
    {
        var here = new DirectoryInfo(AppContext.BaseDirectory);

        while (here is not null && !File.Exists(Path.Combine(here.FullName, "Meows.sln")))
            here = here.Parent;

        Assert.NotNull(here);
        return here;
    }

    /// <summary>
    /// A view with its comments taken out. The template explains {m:Tr key} in a comment, and a
    /// comment is the one place a colour or a key can be written down without being asked for.
    /// </summary>
    private static string Markup(string view) =>
        Regex.Replace(File.ReadAllText(view), "<!--.*?-->", "", RegexOptions.Singleline);

    private static List<string> Views() =>
        Repository().GetFiles("*.axaml", SearchOption.AllDirectories)
            .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => f.Name != "Palette.axaml")
            .Select(f => f.FullName)
            .ToList();

    private static Dictionary<string, List<string>> PaletteTokens()
    {
        var file = Path.Combine(Repository().FullName, "Meows", "Themes", "Palette.axaml");
        Assert.True(File.Exists(file), file);

        var document = XDocument.Load(file);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var byVariant = new Dictionary<string, List<string>>();
        foreach (var dictionary in document.Descendants()
                     .Where(e => e.Name.LocalName == "ResourceDictionary" && e.Attribute(x + "Key") is not null))
        {
            byVariant[dictionary.Attribute(x + "Key")!.Value] = dictionary.Elements()
                .Select(e => e.Attribute(x + "Key")?.Value)
                .Where(k => k is not null)
                .Select(k => k!)
                .ToList();
        }

        return byVariant;
    }

    [Fact]
    public void The_palette_answers_for_both_themes()
    {
        var byVariant = PaletteTokens();

        Assert.Equal(["Dark", "Light"], byVariant.Keys.Order());

        // A token defined in only one of them is invisible in the other, which is worse than a
        // colour being wrong: it is a control with no background at all.
        Assert.Empty(byVariant["Dark"].Except(byVariant["Light"]));
        Assert.Empty(byVariant["Light"].Except(byVariant["Dark"]));
    }

    [Fact]
    public void Every_colour_a_view_asks_for_is_in_the_palette()
    {
        var known = PaletteTokens()["Dark"].ToHashSet(StringComparer.Ordinal);
        var missing = new List<string>();

        foreach (var view in Views())
        {
            var text = Markup(view);
            foreach (Match match in Regex.Matches(text, @"\{DynamicResource (Meows[A-Za-z]+)\}"))
            {
                if (!known.Contains(match.Groups[1].Value))
                    missing.Add($"{Path.GetFileName(view)}: {match.Groups[1].Value}");
            }
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void No_view_still_has_a_colour_written_into_it()
    {
        // One hex in one view is how there came to be a hundred and ninety of them, and every one
        // of those was a control that could not follow a theme.
        var stragglers = Views()
            .Where(v => Regex.IsMatch(Markup(v), "\"#[0-9A-Fa-f]{6,8}\""))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(stragglers);
    }

    [Fact]
    public void Every_string_a_view_asks_for_is_in_a_catalogue()
    {
        var known = Repository().GetFiles("Strings.en.json", SearchOption.AllDirectories)
            .SelectMany(f => JsonSerializer.Deserialize<Dictionary<string, string>>(f.OpenRead())!.Keys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.NotEmpty(known);

        var missing = new List<string>();
        foreach (var view in Views())
        {
            var text = Markup(view);
            foreach (Match match in Regex.Matches(text, @"\{m:Tr ([A-Za-z0-9_.\-]+)\}"))
            {
                if (!known.Contains(match.Groups[1].Value))
                    missing.Add($"{Path.GetFileName(view)}: {match.Groups[1].Value}");
            }
        }

        Assert.Empty(missing);
    }

    /// <summary>
    /// The plugin icons are emoji, and Windows has two fonts holding those characters: Segoe UI
    /// Symbol in monochrome outlines and Segoe UI Emoji in colour. Left to fallback, the first
    /// layout picked the outline for some of them and a later one picked the colour, so hovering
    /// a tab changed its icon and left it changed. Naming the font is the whole fix, and it is
    /// exactly the sort of attribute that gets left off the next place icons are drawn.
    /// </summary>
    [Fact]
    public void Wherever_an_icon_is_drawn_the_font_is_named()
    {
        var bare = new List<string>();
        var found = 0;

        foreach (var view in Views())
        {
            foreach (Match match in Regex.Matches(Markup(view),
                         @"<TextBlock\b[^>]*?/>", RegexOptions.Singleline))
            {
                if (!match.Value.Contains("{Binding Icon}", StringComparison.Ordinal))
                    continue;

                found++;
                if (!match.Value.Contains("FontFamily", StringComparison.Ordinal))
                    bare.Add(Path.GetFileName(view));
            }
        }

        Assert.Empty(bare);

        // Guards the search. A regex that matches nothing would pass with no icons examined.
        Assert.Equal(2, found);
    }

    [Fact]
    public void The_views_really_were_read()
    {
        // Guards every check above. A wrong path finds no files, finds no problems, and passes.
        var views = Views();

        Assert.True(views.Count >= 10, $"only found {views.Count} views");
        Assert.Contains(views, v => Path.GetFileName(v) == "KibbleView.axaml");
    }
}
