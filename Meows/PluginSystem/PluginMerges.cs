using Meows.Plugins.Abstractions;
using Meows.Services;

namespace Meows.Plugins;

/// <summary>
/// Carrying over what belonged to a plugin that another has taken in as a page: Catnip and
/// Mouser into Litter, Weigh-In into Chonk, Perch into Telegram Poster. Worked out from the
/// plugins themselves (a <see cref="PagedPlugin"/> says what it absorbed), so a plugin from
/// anywhere that does the same gets the same care.
///
/// Every step is safe to run again and does nothing the second time, so it runs on every scan
/// rather than keeping a note of having run.
/// </summary>
public static class PluginMerges
{
    /// <summary>The suffix a page's old data folder gets once its contents have moved, kept as a copy.</summary>
    public const string MovedSuffix = ".merged";

    /// <summary>Absorbed id to the id of the plugin that absorbed it.</summary>
    public static IReadOnlyDictionary<string, string> Map(IEnumerable<PluginDescriptor> found)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in found)
        {
            if (descriptor.Plugin is not PagedPlugin paged)
                continue;
            foreach (var page in paged.Absorbed)
                map[page.Id] = paged.Id;
        }
        return map;
    }

    /// <summary>
    /// A plugin found on its own whose id another has absorbed: an old copy left in the plugins
    /// folder by an unzip over the top. Shown as refused, with where it went, rather than run twice.
    /// </summary>
    public static IReadOnlyList<PluginDescriptor> RefuseAbsorbed(IReadOnlyList<PluginDescriptor> found, IReadOnlyDictionary<string, string> map) =>
        [.. found.Select(d => map.TryGetValue(d.Id, out var into) && d.Plugin is not PagedPlugin
            ? PluginDescriptor.Incompatible(d.AssemblyPath,
                MeowsText.Current.Format("plugins.absorbed", d.DisplayName, found.FirstOrDefault(f => f.Id == into)?.DisplayName ?? into),
                d.Provenance)
            : d)];

    /// <summary>Moves everything of the absorbed ids over. Returns what it did, for the log.</summary>
    public static IReadOnlyList<string> Apply(IReadOnlyDictionary<string, string> map, ShellSettings settings,
        ShellPreferences preferences, MeowsStore? store)
    {
        var said = new List<string>();
        if (map.Count == 0)
            return said;

        foreach (var (old, into) in map)
        {
            if (MoveData(settings, old, into) is { } moved)
                said.Add(moved);
            if (store?.Retag(old, into) is > 0 and var rows)
                said.Add($"Moved {rows} history line(s) of {old} to {into}.");
        }

        var activated = settings.LoadActivatedPlugins();
        if (activated.Any(map.ContainsKey))
        {
            settings.SaveActivatedPlugins(activated.Select(id => Mapped(map, id)).Distinct(StringComparer.OrdinalIgnoreCase));
            said.Add("Switched on what the absorbed plugins were switched on as.");
        }

        if (MapPreferences(map, preferences))
        {
            settings.SavePreferences(preferences);
            said.Add("Carried the absorbed plugins over in personas, rules, the strip and Home.");
        }

        return said;
    }

    private static string Mapped(IReadOnlyDictionary<string, string> map, string id) =>
        map.TryGetValue(id, out var into) ? into : id;

    private static List<string> Mapped(IReadOnlyDictionary<string, string> map, List<string> ids) =>
        [.. ids.Select(id => Mapped(map, id)).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Personas, rules, the strip's order and groups, Home's order and hidden, and pop-outs.</summary>
    public static bool MapPreferences(IReadOnlyDictionary<string, string> map, ShellPreferences preferences)
    {
        bool Touches(IEnumerable<string> ids) => ids.Any(map.ContainsKey);
        var changed = false;

        foreach (var persona in preferences.Personas.Where(p => Touches(p.PluginIds)))
        {
            persona.PluginIds = Mapped(map, persona.PluginIds);
            changed = true;
        }

        foreach (var rule in preferences.Rules)
        {
            if (map.TryGetValue(rule.Source, out var source))
            {
                rule.Source = source;
                changed = true;
            }
            if (map.TryGetValue(rule.Target, out var target))
            {
                rule.Target = target;
                changed = true;
            }
        }

        if (Touches(preferences.TabOrder))
        {
            preferences.TabOrder = Mapped(map, preferences.TabOrder);
            changed = true;
        }
        if (Touches(preferences.HomeOrder))
        {
            preferences.HomeOrder = Mapped(map, preferences.HomeOrder);
            changed = true;
        }
        if (Touches(preferences.HomeHidden))
        {
            preferences.HomeHidden = Mapped(map, preferences.HomeHidden);
            changed = true;
        }
        if (Touches(preferences.PoppedOutTabs))
        {
            preferences.PoppedOutTabs = Mapped(map, preferences.PoppedOutTabs);
            changed = true;
        }
        foreach (var old in preferences.TabGroupOf.Keys.Where(map.ContainsKey).ToList())
        {
            preferences.TabGroupOf.Remove(old);
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// The absorbed plugin's folder, copied to where its page now reads it, under the main
    /// plugin's folder and named after its old id, unless the page already has one. The old
    /// folder is then renamed aside rather than deleted: it is the copy to go back to.
    /// </summary>
    private static string? MoveData(ShellSettings settings, string old, string into)
    {
        var from = Path.Combine(settings.Root, "plugins", Safe(old));
        if (!Directory.Exists(from))
            return null;

        var to = Path.Combine(settings.Root, "plugins", Safe(into), Safe(old));
        try
        {
            if (!Directory.Exists(to) || !Directory.EnumerateFileSystemEntries(to).Any())
                Copy(from, to);

            var aside = from + MovedSuffix;
            if (Directory.Exists(aside))
                Directory.Delete(aside, recursive: true);
            Directory.Move(from, aside);
            return $"Moved {old}'s settings and data to {to}; the original is kept as {aside}.";
        }
        catch (Exception ex)
        {
            return $"Could not move {old}'s settings and data to {into}: {ex.Message}";
        }
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static string Safe(string id) =>
        string.Concat(id.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
