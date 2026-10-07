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
}
