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
