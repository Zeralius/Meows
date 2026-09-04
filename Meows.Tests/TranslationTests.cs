using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meows.Plugins.Abstractions;
using Meows.Services;

namespace Meows.Tests;

/// <summary>
/// The string table itself: what it answers, what it falls back to, and whether it tells anyone
/// when the language changes.
///
/// These use their own <see cref="Translations"/> rather than the shared one the module
/// initialiser installs, because switching language on that would change the answers under every
/// other test running at the same time.
/// </summary>
public class TranslationTests
{
    private static Translations Table() => TestStrings.Load();

    [Fact]
    public void A_key_nobody_has_comes_back_as_itself()
    {
        // Visibly wrong on screen rather than blank, and never an exception from inside a binding
        // where nothing would report it.
        Assert.Equal("nothing.like.this", Table()["nothing.like.this"]);
    }

    [Fact]
    public void English_is_the_language_until_told_otherwise()
    {
        Assert.Equal("en", Table().Language);
        Assert.Equal("Settings", Table()["settings.title"]);
    }

    [Fact]
    public void Switching_language_changes_the_answers()
    {
        var text = Table();
        text.Use("de");

        Assert.Equal("de", text.Language);
        Assert.Equal("Einstellungen", text["settings.title"]);
    }

    [Fact]
    public void Following_the_system_resolves_to_a_language_we_actually_ship()
    {
        var text = Table();
        text.Use("system");

        Assert.Contains(text.Language, Translations.Shipped);
    }

    [Fact]
    public void A_language_we_do_not_ship_falls_back_to_english()
    {
        var text = Table();
        text.Use("de");
        text.Use("fr");

        Assert.Equal("en", text.Language);
    }

    [Fact]
    public void An_untranslated_key_reads_in_english_rather_than_vanishing()
    {
        var text = new Translations();
        text.Add(typeof(TranslationTests).Assembly);

        // Only English has it, so German has to borrow it. Half a window in the wrong language is
        // a nuisance; half a window of dotted identifiers is unusable.
        text.Use("de");
        Assert.Equal("Only in English", text["test.englishonly"]);
    }

    [Fact]
    public void A_language_change_is_announced_so_the_window_can_repaint()
    {
        var text = Table();
        var told = 0;
        text.PropertyChanged += (_, _) => told++;

        text.Use("de");

        Assert.True(told > 0);
    }

    [Fact]
    public void Placeholders_are_filled_in()
    {
        Assert.Equal("Plugin contract 1.2.3", Table().Format("plugins.contract", "1.2.3"));
    }

    [Fact]
    public void A_translation_with_the_wrong_placeholders_shows_the_template_instead_of_throwing()
    {
        var complaints = new List<string>();
        var text = new Translations(complaints.Add);
        text.Add(typeof(TranslationTests).Assembly);

        // One placeholder in the string, nothing handed to it. Formatting throws; a binding is
        // the worst possible place for that to happen.
        var said = text.Format("test.needsone");

        Assert.Equal("Wants {0}", said);
        Assert.NotEmpty(complaints);
    }

    /// <summary>
    /// The bindable form of a key. The whole window hangs off this: binding to the table's
    /// indexer instead looks tidier and silently never updates, which is how the first attempt
    /// at this shipped a settings page that changed language everywhere except in itself.
    /// </summary>
    [Fact]
    public void A_bindable_string_is_told_when_the_language_changes()
    {
        var entry = MeowsText.Entry("settings.title");
        var told = 0;
        entry.PropertyChanged += (_, _) => told++;

        var fresh = Table();
        fresh.Use("de");
        MeowsText.Use(fresh);

        try
        {
            Assert.True(told > 0);
            Assert.Equal("Einstellungen", entry.Value);
        }
        finally
        {
            // Everything else in the suite reads English.
            TestStrings.Install();
        }
    }

    [Fact]
    public void The_same_key_asked_for_twice_is_the_same_object()
    {
        // Otherwise a key used on twenty cards is twenty objects to notify, for no gain.
        Assert.Same(MeowsText.Entry("plugins.title"), MeowsText.Entry("plugins.title"));
    }
}

/// <summary>
/// The catalogues as shipped. A missing German string is not a crash, it just quietly reads in
/// English, which is exactly the kind of thing nobody notices until someone else does.
/// </summary>
public class CatalogueTests
{
    private static readonly Assembly[] Carriers =
    [
        typeof(Translations).Assembly,
        typeof(Disk.FolderInspector).Assembly,
        typeof(Bot.QueueRunway).Assembly,
        typeof(Plugins.Chonk.ChonkPlugin).Assembly,
        typeof(Plugins.Kibble.KibblePlugin).Assembly,
        typeof(Plugins.Litter.LitterPlugin).Assembly,
        typeof(Plugins.Molt.MoltPlugin).Assembly,
        typeof(Plugins.Mouser.MouserPlugin).Assembly,
        typeof(Plugins.Purrge.PurrgePlugin).Assembly,
        typeof(Plugins.Saucer.SaucerPlugin).Assembly,
        typeof(Plugins.TelegramPoster.TelegramPosterPlugin).Assembly,
        typeof(Plugins.Birdwatch.BirdwatchPlugin).Assembly,
    ];

    private static Dictionary<string, string> Read(Assembly assembly, string language)
    {
        var name = assembly.GetManifestResourceNames()
            .SingleOrDefault(n => n.EndsWith($"Strings.{language}.json", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(name);

        using var stream = assembly.GetManifestResourceStream(name)!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }

    /// <summary>
    /// Embedded rather than sitting beside the exe, and named the way the loader expects. MSBuild
    /// reads the middle of Strings.de.json as a culture and, left to itself, hides both files in
    /// satellite assemblies where nothing ever looks.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryCarrier))]
    public void Every_assembly_ships_both_languages(string assemblyName)
    {
        var assembly = Carriers.Single(a => a.GetName().Name == assemblyName);

        Assert.NotEmpty(Read(assembly, "en"));
        Assert.NotEmpty(Read(assembly, "de"));
    }

    [Theory]
    [MemberData(nameof(EveryCarrier))]
    public void German_says_everything_english_says(string assemblyName)
    {
        var assembly = Carriers.Single(a => a.GetName().Name == assemblyName);
        var english = Read(assembly, "en");
        var german = Read(assembly, "de");

        Assert.Empty(english.Keys.Except(german.Keys));
        Assert.Empty(german.Keys.Except(english.Keys));
    }

    /// <summary>
    /// A translation that drops a {0}, or invents a {2}, throws when it is formatted. Catching it
    /// here beats catching it when somebody switches language and a status line goes blank.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryCarrier))]
    public void Both_languages_take_the_same_values(string assemblyName)
    {
        var assembly = Carriers.Single(a => a.GetName().Name == assemblyName);
        var english = Read(assembly, "en");
        var german = Read(assembly, "de");

        foreach (var (key, value) in english)
        {
            Assert.Equal(Slots(value), Slots(german[key]));
        }
    }

    private static string Slots(string text) =>
        string.Join(",", Regex.Matches(text, @"\{(\d+)\}").Select(m => m.Groups[1].Value).Order());

    [Fact]
    public void No_two_assemblies_claim_the_same_key()
    {
        // They are merged into one table, so a clash means whichever plugin loads last wins and
        // the other one silently reads somebody else's words.
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var assembly in Carriers)
        {
            var name = assembly.GetName().Name!;
            foreach (var key in Read(assembly, "en").Keys)
            {
                Assert.False(seen.TryGetValue(key, out var owner),
                    $"'{key}' is in both {owner} and {name}.");
                seen[key] = name;
            }
        }
    }

    /// <summary>
    /// Guards the list above. Birdwatch was left out of it and out of the table the rest of the
    /// suite reads, so its sixty odd strings went unchecked in both languages and every test
    /// touching them was quietly comparing against bare keys. Nothing failed, which is the
    /// problem. A list of assemblies typed out by hand needs something asking whether it is
    /// still the whole list.
    /// </summary>
    [Fact]
    public void Every_plugin_that_ships_is_on_the_list()
    {
        var here = Path.GetDirectoryName(typeof(CatalogueTests).Assembly.Location)!;
        var built = Directory.GetFiles(here, "Meows.Plugins.*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n != "Meows.Plugins.Abstractions")
            .ToList();

        // Guards the guard. A wrong folder would find nothing and pass.
        Assert.True(built.Count >= 9, $"Only found {built.Count} plugins in {here}.");

        var listed = Carriers.Select(a => a.GetName().Name).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(built.Except(listed));
    }

    public static TheoryData<string> EveryCarrier()
    {
        var data = new TheoryData<string>();
        foreach (var assembly in Carriers)
            data.Add(assembly.GetName().Name!);
        return data;
    }
}
