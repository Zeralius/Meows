namespace Meows.Services;

/// <summary>One named set of switched-on plugins, as it is kept.</summary>
public sealed class PersonaSetting
{
    public string Name { get; set; } = "";

    public List<string> PluginIds { get; set; } = [];
}

/// <summary>
/// Personas: named sets of switched-on plugins, and the arithmetic of matching and applying
/// them. Pure, so the matching a test asserts is the matching the tab shows: what is named
/// against what is installed and on, with missing plugins ignored rather than uninstalled.
/// </summary>
public static class Personas
{
    /// <summary>
    /// The ones Meows comes with, by name key and plugin ids. Fixed lists rather than "everything
    /// about Gaming", so they are personas like any other once added: renamed, changed or deleted
    /// as the person likes, and a plugin installed later never joins one by itself. Purr is in
    /// every one, since what is being watched matters whatever the hat. An id that is not
    /// installed is ignored when applied, as it is in any persona.
    /// </summary>
    public static IReadOnlyList<(string NameKey, string[] PluginIds)> ReadyMade { get; } =
    [
        ("persona.gamer", ["meows.larder", "meows.backlog", "meows.screenshot", "meows.nest", "meows.purr"]),
        ("persona.gamemaster", ["meows.familiar", "meows.collar", "meows.nest", "meows.purr"]),
        ("persona.household", ["meows.collar", "meows.basket", "meows.pantry", "meows.naptime", "meows.tin",
            "meows.bookshelf", "meows.vet", "meows.purr"]),
        ("persona.creator", ["meows.scruff", "meows.birdwatch", "meows.saucer", "meows.kibble", "meows.perch",
            "meows.portion", "meows.telegram-poster", "meows.screenshot", "meows.purr"]),
        ("persona.developer", ["meows.cattery", "meows.trail", "meows.molt", "meows.chonk", "meows.purr"]),
    ];

    /// <summary>
    /// Adds the ready-made personas that are not there, named in the language of the moment, and
    /// says how many it added. One already there by that name, ready-made or the person's own, is
    /// left exactly as it is, so this can be offered again after one was deleted without undoing
    /// anything that was changed.
    /// </summary>
    public static int AddReadyMade(List<PersonaSetting> personas, Func<string, string> name)
    {
        var added = 0;
        foreach (var (key, ids) in ReadyMade)
        {
            var named = name(key);
            if (personas.Any(p => string.Equals(p.Name, named, StringComparison.OrdinalIgnoreCase)))
                continue;
            personas.Add(new PersonaSetting { Name = named, PluginIds = [.. ids] });
            added++;
        }
        return added;
    }

    /// <summary>
    /// The persona the window matches right now, or null for a custom mix. A persona matches
    /// when what it names, among the plugins installed, is exactly what is on.
    /// </summary>
    public static string? Match(
        IReadOnlySet<string> activated,
        IEnumerable<string> installed,
        IReadOnlyList<PersonaSetting> personas)
    {
        var here = new HashSet<string>(installed, StringComparer.OrdinalIgnoreCase);
        foreach (var persona in personas)
        {
            var named = persona.PluginIds.Where(here.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (named.SetEquals(activated))
                return persona.Name;
        }
        return null;
    }

    /// <summary>
    /// What applying the persona switches on and off: what it names that is installed and off,
    /// and what is on that it does not name. Missing plugins are ignored, never uninstalled.
    /// </summary>
    public static (IReadOnlyList<string> TurnOn, IReadOnlyList<string> TurnOff) Apply(
        IReadOnlySet<string> activated,
        IEnumerable<string> installed,
        PersonaSetting persona)
    {
        var here = new HashSet<string>(installed, StringComparer.OrdinalIgnoreCase);
        var wanted = persona.PluginIds.Where(here.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (
            wanted.Where(id => !activated.Contains(id)).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList(),
            activated.Where(id => here.Contains(id) && !wanted.Contains(id)).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList());
    }
}
