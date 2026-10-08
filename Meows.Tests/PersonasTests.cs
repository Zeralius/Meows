using Meows.Services;

namespace Meows.Tests;

/// <summary>
/// Personas: named sets of switched-on plugins. Matching what is on, applying what is named,
/// and missing plugins ignored rather than uninstalled.
/// </summary>
public sealed class PersonasTests
{
    private static List<PersonaSetting> Set() =>
    [
        new() { Name = "Work", PluginIds = ["meows.collar", "meows.basket"] },
        new() { Name = "Table", PluginIds = ["meows.familiar", "meows.gone"] },
    ];

    private static readonly string[] Installed = ["meows.collar", "meows.basket", "meows.familiar"];

    [Fact]
    public void The_window_matches_the_persona_whose_set_is_on()
    {
        Assert.Equal("Work", Meows.Services.Personas.Match(new HashSet<string>(["meows.collar", "meows.basket"], StringComparer.OrdinalIgnoreCase), Installed, Set()));
        Assert.Null(Meows.Services.Personas.Match(new HashSet<string>(["meows.collar"], StringComparer.OrdinalIgnoreCase), Installed, Set()));
        Assert.Null(Meows.Services.Personas.Match(new HashSet<string>(StringComparer.OrdinalIgnoreCase), Installed, Set()));
        Assert.Null(Meows.Services.Personas.Match(new HashSet<string>(["meows.collar", "meows.basket"], StringComparer.OrdinalIgnoreCase), Installed, []));
    }

    [Fact]
    public void A_missing_plugin_neither_matches_nor_uninstalls()
    {
        // "Table" names meows.gone, which is not installed: matching reads it as the two that are.
        Assert.Equal("Table", Meows.Services.Personas.Match(new HashSet<string>(["meows.familiar"], StringComparer.OrdinalIgnoreCase), Installed, Set()));

        var (on, off) = Meows.Services.Personas.Apply(new HashSet<string>(["meows.collar"], StringComparer.OrdinalIgnoreCase), Installed,
            new PersonaSetting { Name = "Table", PluginIds = ["meows.familiar", "meows.gone"] });

        Assert.Equal(["meows.familiar"], on);
        Assert.Equal(["meows.collar"], off);
    }

    [Fact]
    public void Applying_switches_on_what_is_named_and_off_what_is_not()
    {
        var (on, off) = Meows.Services.Personas.Apply(new HashSet<string>(["meows.collar", "meows.familiar"], StringComparer.OrdinalIgnoreCase), Installed,
            new PersonaSetting { Name = "Work", PluginIds = ["meows.collar", "meows.basket"] });

        Assert.Equal(["meows.basket"], on);
        Assert.Equal(["meows.familiar"], off);
    }

    [Fact]
    public void Applying_what_matches_changes_nothing()
    {
        var (on, off) = Meows.Services.Personas.Apply(new HashSet<string>(["meows.collar", "meows.basket"], StringComparer.OrdinalIgnoreCase), Installed,
            new PersonaSetting { Name = "Work", PluginIds = ["meows.collar", "meows.basket"] });

        Assert.Empty(on);
        Assert.Empty(off);
    }

    private static string English(string key) => TestStrings.Load()[key];

    [Fact]
    public void The_ready_made_ones_are_added_once_by_name_and_in_the_words_of_the_moment()
    {
        var personas = new List<PersonaSetting>();

        Assert.Equal(5, Meows.Services.Personas.AddReadyMade(personas, English));
        Assert.Equal(["Gamer", "Game master", "Household", "Creator", "Developer"], personas.Select(p => p.Name));
        Assert.Equal(0, Meows.Services.Personas.AddReadyMade(personas, English));
        Assert.Equal(5, personas.Count);
    }

    [Fact]
    public void Adding_them_again_brings_back_a_deleted_one_and_leaves_a_changed_one_alone()
    {
        var personas = new List<PersonaSetting> { new() { Name = "gamer", PluginIds = ["meows.larder"] } };
        Meows.Services.Personas.AddReadyMade(personas, English);
        personas.RemoveAll(p => p.Name == "Household");

        Assert.Equal(1, Meows.Services.Personas.AddReadyMade(personas, English));
        Assert.Equal(["meows.larder"], personas.Single(p => p.Name == "gamer").PluginIds);
        Assert.Contains(personas, p => p.Name == "Household");
    }

    /// <summary>A ready-made persona naming a plugin that does not exist would quietly never switch it on.</summary>
    [Fact]
    public void Every_plugin_a_ready_made_one_names_ships_here()
    {
        var shipped = ShippedPlugins.Types
            .Select(t => ((Meows.Plugins.Abstractions.IMeowsPlugin)Activator.CreateInstance(t)!).Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, ids) in Meows.Services.Personas.ReadyMade)
        {
            Assert.NotEqual(key, English(key));
            Assert.All(ids, id => Assert.Contains(id, shipped));
        }
    }
}
