using Meows.Services;

namespace Meows.Tests;

/// <summary>
/// What the tab strip shows, worked out from the tabs that exist, what each declares and
/// whatever has been rearranged since. All of it is strings, so none of it needs a window.
///
/// This is the part that goes wrong quietly: a tab in two groups at once, a group that empties
/// and lingers, an order that drifts a little every time a plugin is switched on. None of that
/// throws, and all of it is only noticed a week later.
/// </summary>
public sealed class TabGroupingTests
{
    private static readonly Dictionary<string, string> NothingMoved = new();
    private static readonly HashSet<string> NothingShut = [];
    private static readonly Dictionary<string, string> NoColours = new();

    private static IReadOnlyList<ArrangedGroup> Arrange(
        IReadOnlyList<string> tabs,
        IReadOnlyDictionary<string, string> declared,
        IReadOnlyDictionary<string, string>? moved = null,
        IReadOnlyList<string>? groupOrder = null,
        IReadOnlyList<string>? tabOrder = null,
        IReadOnlySet<string>? collapsed = null) =>
        TabGrouping.Arrange(tabs, declared, moved ?? NothingMoved, groupOrder ?? [], tabOrder ?? [],
            NoColours, collapsed ?? NothingShut);

    [Fact]
    public void A_fresh_install_is_grouped_by_what_the_plugins_declare()
    {
        // Nothing is remembered, so the whole arrangement comes from the category each plugin
        // already declares for its card on the Plugins tab. This is the case that decides
        // whether anyone ever has to arrange anything by hand.
        var groups = Arrange(
            ["shell.tab.home", "shell.tab.plugins", "meows.chonk", "meows.purrge", "meows.perch"],
            new Dictionary<string, string>
            {
                ["shell.tab.plugins"] = TabGroups.Shell,
                ["meows.chonk"] = "group.disk",
                ["meows.purrge"] = "group.disk",
                ["meows.perch"] = "group.bot",
            });

        Assert.Equal([TabGroups.Shell, "group.disk", "group.bot"], groups.Select(g => g.Key));
        Assert.Equal(["meows.chonk", "meows.purrge"], groups[1].Tabs);
    }

    [Fact]
    public void Home_is_in_no_group_at_all()
    {
        var groups = Arrange(["shell.tab.home", "meows.chonk"],
            new Dictionary<string, string> { ["meows.chonk"] = "group.disk" });

        Assert.DoesNotContain("shell.tab.home", groups.SelectMany(g => g.Tabs));
        Assert.Equal("shell.tab.home", TabGrouping.Visible(["shell.tab.home", "meows.chonk"], groups)[0]);
    }

    [Fact]
    public void A_plugin_that_names_no_category_lands_with_everything_else()
    {
        var groups = Arrange(["someone.weather"], new Dictionary<string, string>());

        Assert.Equal(TabGroups.Other, Assert.Single(groups).Key);
    }

    [Fact]
    public void Where_a_tab_was_put_beats_what_it_declared()
    {
        var groups = Arrange(
            ["meows.chonk", "meows.perch"],
            new Dictionary<string, string> { ["meows.chonk"] = "group.disk", ["meows.perch"] = "group.bot" },
            moved: new Dictionary<string, string> { ["meows.perch"] = "group.disk" });

        var only = Assert.Single(groups);
        Assert.Equal("group.disk", only.Key);
        Assert.Equal(["meows.chonk", "meows.perch"], only.Tabs);
    }

    [Fact]
    public void A_group_whose_plugins_are_all_switched_off_is_not_shown_empty()
    {
        // The group is still remembered, with its colour and its name. It simply has nothing in
        // it right now, and a chip standing for nothing is worse than no chip.
        var groups = Arrange(
            ["meows.chonk"],
            new Dictionary<string, string> { ["meows.chonk"] = "group.disk" },
            groupOrder: ["group.bot", "group.disk"]);

        Assert.Equal("group.disk", Assert.Single(groups).Key);
    }

    [Fact]
    public void A_remembered_order_is_kept_and_anything_new_goes_at_the_end()
    {
        // The case that would otherwise drift: a plugin switched on today must not land in the
        // middle of an order somebody arranged last week.
        var groups = Arrange(
            ["meows.perch", "meows.chonk", "meows.catnip"],
            new Dictionary<string, string>
            {
                ["meows.perch"] = "group.bot",
                ["meows.chonk"] = "group.disk",
                ["meows.catnip"] = "group.disk",
            },
            groupOrder: ["group.disk", "group.bot"],
            tabOrder: ["meows.chonk"]);

        Assert.Equal(["group.disk", "group.bot"], groups.Select(g => g.Key));
        Assert.Equal(["meows.chonk", "meows.catnip"], groups[0].Tabs);
    }

    [Fact]
    public void A_remembered_entry_that_is_gone_leaves_no_hole()
    {
        Assert.Equal(["b", "a"], TabGrouping.Sort(["a", "b"], ["b", "vanished", "a"]));
        Assert.Equal(["a", "b"], TabGrouping.Sort(["a", "b"], []));
    }

    [Fact]
    public void A_shut_group_contributes_nothing_to_the_number_keys()
    {
        var tabs = new[] { "shell.tab.home", "meows.chonk", "meows.perch" };
        var groups = Arrange(tabs,
            new Dictionary<string, string> { ["meows.chonk"] = "group.disk", ["meows.perch"] = "group.bot" },
            collapsed: new HashSet<string> { "group.disk" });

        // Ctrl+2 lands on Perch, because Chonk is behind a chip. Counting past what cannot be
        // seen would make the number depend on something invisible.
        Assert.Equal(["shell.tab.home", "meows.perch"], TabGrouping.Visible(tabs, groups));
    }

    [Fact]
    public void A_step_within_a_group_stays_in_it()
    {
        var groups = Arrange(["a", "b", "c"],
            new Dictionary<string, string> { ["a"] = "g1", ["b"] = "g1", ["c"] = "g1" });

        var (order, landing) = TabGrouping.Nudge("c", -1, groups);

        Assert.Equal(["a", "c", "b"], order);
        Assert.Null(landing);
    }

    [Fact]
    public void A_step_past_the_edge_of_a_group_moves_the_tab_into_the_next_one()
    {
        // This is what makes two menu entries enough to put a tab anywhere: walking it off the
        // end of its group is how it changes group.
        var groups = Arrange(["a", "b"],
            new Dictionary<string, string> { ["a"] = "g1", ["b"] = "g2" });

        var (order, landing) = TabGrouping.Nudge("a", 1, groups);

        Assert.Equal(["b", "a"], order);
        Assert.Equal("g2", landing);
    }

    [Fact]
    public void A_step_off_either_end_of_the_strip_does_nothing()
    {
        var groups = Arrange(["a", "b"], new Dictionary<string, string> { ["a"] = "g1", ["b"] = "g1" });

        Assert.Null(TabGrouping.Nudge("a", -1, groups).NewGroup);
        Assert.Equal(["a", "b"], TabGrouping.Nudge("a", -1, groups).TabOrder);
        Assert.Equal(["a", "b"], TabGrouping.Nudge("b", 1, groups).TabOrder);
    }

    [Fact]
    public void A_dropped_tab_lands_on_the_side_of_the_target_the_pointer_was_nearest()
    {
        var groups = Arrange(["a", "b", "c"],
            new Dictionary<string, string> { ["a"] = "g1", ["b"] = "g1", ["c"] = "g1" });

        Assert.Equal(["b", "a", "c"], TabGrouping.Place(groups, "a", "b", after: true).TabOrder);
        Assert.Equal(["a", "c", "b"], TabGrouping.Place(groups, "c", "b", after: false).TabOrder);
    }

    [Fact]
    public void A_tab_dropped_into_another_group_joins_it()
    {
        var groups = Arrange(["a", "b"], new Dictionary<string, string> { ["a"] = "g1", ["b"] = "g2" });

        var (order, landing) = TabGrouping.Place(groups, "a", "b", after: true);

        Assert.Equal(["b", "a"], order);
        Assert.Equal("g2", landing);
    }

    [Fact]
    public void A_tab_dropped_within_its_own_group_does_not_change_group()
    {
        var groups = Arrange(["a", "b"], new Dictionary<string, string> { ["a"] = "g1", ["b"] = "g1" });

        Assert.Null(TabGrouping.Place(groups, "a", "b", after: true).NewGroup);
    }

    [Fact]
    public void Dropping_a_tab_on_itself_changes_nothing()
    {
        // The commonest accident: a click that travelled six pixels. It must be a no-op rather
        // than a reorder nobody asked for.
        var groups = Arrange(["a", "b"], new Dictionary<string, string> { ["a"] = "g1", ["b"] = "g1" });

        var (order, landing) = TabGrouping.Place(groups, "a", "a", after: true);

        Assert.Equal(["a", "b"], order);
        Assert.Null(landing);
    }

    [Fact]
    public void Removing_the_tab_first_does_not_shift_where_it_lands()
    {
        // Worked out naively, taking a tab from in front of its target and then inserting at the
        // target's old index puts it one place too far along. This is that case, both ways.
        var groups = Arrange(["a", "b", "c", "d"],
            new Dictionary<string, string> { ["a"] = "g", ["b"] = "g", ["c"] = "g", ["d"] = "g" });

        Assert.Equal(["b", "c", "a", "d"], TabGrouping.Place(groups, "a", "c", after: true).TabOrder);
        Assert.Equal(["a", "d", "b", "c"], TabGrouping.Place(groups, "d", "b", after: false).TabOrder);
    }

    [Fact]
    public void A_dropped_group_takes_its_place_in_the_order()
    {
        string[] groups = ["one", "two", "three"];

        Assert.Equal(["two", "one", "three"], TabGrouping.Reorder(groups, "one", "two", after: true));
        Assert.Equal(["one", "three", "two"], TabGrouping.Reorder(groups, "three", "two", after: false));
        Assert.Equal(["one", "two", "three"], TabGrouping.Reorder(groups, "two", "two", after: true));
        Assert.Equal(["one", "two", "three"], TabGrouping.Reorder(groups, "two", "nowhere", after: true));
    }

    [Fact]
    public void Every_colour_resolves_and_an_unknown_one_falls_back()
    {
        foreach (var colour in GroupColours.All)
            Assert.StartsWith("MeowsGroup", GroupColours.Resource(colour));

        // A preferences file edited by hand should give a dull group, not a window that will
        // not draw.
        Assert.Equal("MeowsGroupSlate", GroupColours.Resource("chartreuse"));
        Assert.Equal("MeowsGroupSlate", GroupColours.Resource(null));
        Assert.Equal("MeowsGroupBlue", GroupColours.Resource("BLUE"));
    }

    [Fact]
    public void A_new_group_takes_a_colour_nothing_else_is_using()
    {
        Assert.Equal("slate", GroupColours.Next([]));
        Assert.Equal("blue", GroupColours.Next(["slate"]));
        Assert.Equal("green", GroupColours.Next(["slate", "blue"]));

        // Past the end of the palette it wraps rather than running out.
        Assert.Contains(GroupColours.Next(GroupColours.All), GroupColours.All);
    }
}
