namespace Meows.Services;

/// <summary>
/// One group's colour, by name rather than by hex, so it resolves against
/// <c>MeowsGroup&lt;Name&gt;</c> in the palette and follows the theme like everything else.
/// </summary>
public static class GroupColours
{
    /// <summary>The order new groups take them in, so two groups made in a row do not match.</summary>
    public static readonly string[] All = ["slate", "blue", "green", "amber", "rose", "violet", "teal"];

    public const string Default = "slate";

    /// <summary>
    /// The palette key for a colour name. An unknown name falls back rather than throwing: a
    /// preferences file edited by hand should give a dull group, not a window that will not draw.
    /// </summary>
    public static string Resource(string? colour)
    {
        var name = Tidy(colour);
        if (!All.Contains(name))
            name = Default;
        return "MeowsGroup" + char.ToUpperInvariant(name[0]) + name[1..];
    }

    private static string Tidy(string? colour) =>
        string.IsNullOrWhiteSpace(colour) ? Default : colour.Trim().ToLowerInvariant();

    /// <summary>The next colour that no group is using, else the one after the last group's.</summary>
    public static string Next(IEnumerable<string> taken)
    {
        var used = taken.Select(Tidy).ToHashSet(StringComparer.Ordinal);
        return All.FirstOrDefault(c => !used.Contains(c)) ?? All[used.Count % All.Length];
    }
}

/// <summary>How big the strip is drawn. Three steps rather than a number, so it cannot be silly.</summary>
public static class TabSizes
{
    public const string Compact = "compact";
    public const string Normal = "normal";
    public const string Large = "large";

    public static readonly string[] All = [Compact, Normal, Large];

    /// <summary>Anything unrecognised reads as the middle one rather than throwing.</summary>
    public static string Tidy(string? size) =>
        All.Contains(size?.Trim().ToLowerInvariant()) ? size!.Trim().ToLowerInvariant() : Normal;
}

/// <summary>
/// Where the shell's own tabs go. A plugin names its own group through <c>IMeowsPlugin.Category</c>
/// and the Plugins tab has been grouping cards by it since 1.3.0; the strip uses the same keys, so
/// a plugin that already sits under *Disk* on its card sits under *Disk* on the strip without
/// anybody arranging anything.
/// </summary>
public static class TabGroups
{
    /// <summary>Plugins, Settings, History and Log: the app's own furniture, collapsible together.</summary>
    public const string Shell = "group.shell";

    /// <summary>What a plugin that named no category gets, the same key the Plugins tab uses.</summary>
    public const string Other = "group.other";

    /// <summary>
    /// Home is never in a group. It is the page the window opens on and the one thing that
    /// should never be behind a collapsed chip, so it is pinned first and has no chip of its own.
    /// </summary>
    public const string HomeKey = "shell.tab.home";

    public static readonly string[] ShellTabs =
        ["shell.tab.plugins", "shell.tab.settings", "shell.tab.history", "shell.tab.log"];
}

/// <summary>One group as the strip needs it: who is in it, what colour, and whether it is shut.</summary>
public sealed record ArrangedGroup(string Key, string Colour, bool IsCollapsed, IReadOnlyList<string> Tabs);

/// <summary>
/// Works out what the strip shows, from the tabs that exist, what each one says it belongs to,
/// and whatever the person has since rearranged.
///
/// Kept apart from the view models because it is the part that can be wrong in ways nobody sees
/// for a week: a tab in two groups, a group that empties and lingers, an order that drifts every
/// time a plugin is switched on. None of it needs Avalonia, so all of it is tested.
/// </summary>
public static class TabGrouping
{
    /// <summary>
    /// The groups, in order, each holding its tabs in order.
    ///
    /// <paramref name="tabs"/> is every tab open now, in the order they were added, which is the
    /// order the shell has always shown them in. <paramref name="declared"/> is what each tab
    /// says it belongs to, the plugin's category or the shell's own group.
    /// <paramref name="moved"/> is where somebody has since put a tab, and wins over what it
    /// declared. <paramref name="groupOrder"/> and <paramref name="tabOrder"/> are what was
    /// remembered; anything not in them keeps the order it arrived in, at the end.
    /// </summary>
    public static IReadOnlyList<ArrangedGroup> Arrange(
        IReadOnlyList<string> tabs,
        IReadOnlyDictionary<string, string> declared,
        IReadOnlyDictionary<string, string> moved,
        IReadOnlyList<string> groupOrder,
        IReadOnlyList<string> tabOrder,
        IReadOnlyDictionary<string, string> colours,
        IReadOnlySet<string> collapsed)
    {
        var byGroup = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var tab in Sort(tabs, tabOrder))
        {
            if (tab == TabGroups.HomeKey)
                continue; // Pinned ahead of every group and never in one.

            var group = Group(tab, declared, moved);
            if (!byGroup.TryGetValue(group, out var list))
            {
                byGroup[group] = list = [];
                order.Add(group);
            }

            list.Add(tab);
        }

        // A group that was remembered but holds nothing right now is left out rather than shown
        // empty: its plugins are switched off, and it comes back with them.
        var arranged = Sort(order, groupOrder)
            .Where(g => byGroup.TryGetValue(g, out var held) && held.Count > 0)
            .Select(g => new ArrangedGroup(
                g,
                colours.TryGetValue(g, out var colour) ? colour : GroupColours.Default,
                collapsed.Contains(g),
                byGroup[g]))
            .ToList();

        return arranged;
    }

    /// <summary>Where one tab belongs: where it was put, else what it said, else with the rest.</summary>
    public static string Group(
        string tab,
        IReadOnlyDictionary<string, string> declared,
        IReadOnlyDictionary<string, string> moved)
    {
        if (moved.TryGetValue(tab, out var chosen) && chosen.Length > 0)
            return chosen;
        if (declared.TryGetValue(tab, out var said) && said.Length > 0)
            return said;
        return TabGroups.Other;
    }

    /// <summary>
    /// Remembered order first, then everything else in the order it arrived. A remembered entry
    /// that is no longer there is skipped rather than leaving a hole, and a new one lands at the
    /// end rather than somewhere unpredictable in the middle.
    /// </summary>
    public static List<string> Sort(IReadOnlyList<string> items, IReadOnlyList<string> remembered)
    {
        var known = items.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sorted = new List<string>();

        foreach (var item in remembered)
        {
            if (known.Remove(item))
                sorted.Add(item);
        }

        foreach (var item in items)
        {
            if (known.Contains(item))
                sorted.Add(item);
        }

        return sorted;
    }

    /// <summary>
    /// Every tab the strip actually shows, in strip order: Home, then each open group's tabs.
    /// A collapsed group contributes nothing, which is what makes Ctrl+1 to Ctrl+9 land on what
    /// can be seen rather than counting past things that are hidden.
    /// </summary>
    public static List<string> Visible(IReadOnlyList<string> tabs, IReadOnlyList<ArrangedGroup> groups)
    {
        var visible = new List<string>();
        if (tabs.Contains(TabGroups.HomeKey))
            visible.Add(TabGroups.HomeKey);

        foreach (var group in groups.Where(g => !g.IsCollapsed))
            visible.AddRange(group.Tabs);

        return visible;
    }

    /// <summary>
    /// Where a dragged tab lands: the order it leaves behind, and the group it has joined if it
    /// crossed into one.
    ///
    /// The target is the tab it was dropped on and <paramref name="after"/> says which side of
    /// it. Dropping a tab on itself, or on the side of a neighbour it already sits on, changes
    /// nothing, which is what keeps a shaky hand from reordering the strip by accident.
    /// </summary>
    public static (List<string> TabOrder, string? NewGroup) Place(
        IReadOnlyList<ArrangedGroup> groups,
        string tab,
        string targetTab,
        bool after)
    {
        var flat = groups.SelectMany(g => g.Tabs).ToList();
        var at = flat.IndexOf(tab);
        var target = flat.IndexOf(targetTab);

        if (at < 0 || target < 0 || tab == targetTab)
            return (flat, null);

        flat.RemoveAt(at);

        // Worked out after the removal, because taking the tab out shifts everything past it.
        var insert = flat.IndexOf(targetTab) + (after ? 1 : 0);
        flat.Insert(insert, tab);

        var from = groups.First(g => g.Tabs.Contains(tab));
        var landing = groups.First(g => g.Tabs.Contains(targetTab));

        return (flat, from.Key == landing.Key ? null : landing.Key);
    }

    /// <summary>
    /// Groups in the order a dragged one leaves them, dropped to one side of another. Dropping a
    /// group on itself changes nothing.
    /// </summary>
    public static List<string> Reorder(IReadOnlyList<string> groups, string moving, string target, bool after)
    {
        var order = groups.ToList();
        var at = order.IndexOf(moving);
        if (at < 0 || !order.Contains(target) || moving == target)
            return order;

        order.RemoveAt(at);
        order.Insert(order.IndexOf(target) + (after ? 1 : 0), moving);
        return order;
    }

    /// <summary>
    /// One step left or right within the whole strip, crossing into the next group when it runs
    /// out of room in this one, which is what makes a menu with two entries enough to put a tab
    /// anywhere at all.
    /// </summary>
    public static (List<string> TabOrder, string? NewGroup) Nudge(
        string tab,
        int direction,
        IReadOnlyList<ArrangedGroup> groups)
    {
        var flat = groups.SelectMany(g => g.Tabs).ToList();
        var at = flat.IndexOf(tab);
        if (at < 0)
            return (flat, null);

        var to = at + direction;
        if (to < 0 || to >= flat.Count)
            return (flat, null);

        var from = groups.First(g => g.Tabs.Contains(tab));
        var landing = groups.First(g => g.Tabs.Contains(flat[to]));

        flat.RemoveAt(at);
        flat.Insert(to, tab);

        // Moving past the edge of a group is how a tab changes group without a menu: the step
        // that would leave it behind the next group's first tab puts it in that group.
        return (flat, from.Key == landing.Key ? null : landing.Key);
    }
}
