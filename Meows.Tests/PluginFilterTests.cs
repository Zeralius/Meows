using Avalonia.Controls;
using Meows.Plugins;
using Meows.Plugins.Abstractions;
using Meows.ViewModels;

namespace Meows.Tests;

/// <summary>
/// The filter on the Plugins tab: which chips there are, how many each counts, and what each
/// lets through. Since 5.1.0 and contract 1.7.0.
/// </summary>
public sealed class PluginFilterTests
{
    private sealed class About(PluginTopics topics) : IMeowsPlugin
    {
        public string Id => "test.about";

        public string DisplayName => "About";

        public string Description => "";

        public string? Icon => null;

        public PluginTopics Topics => topics;

        public Control CreateView(IMeowsHost host) => throw new NotSupportedException();
    }

    /// <summary>Written against a contract from before topics: the interface default has to carry it.</summary>
    private sealed class Older : IMeowsPlugin
    {
        public string Id => "test.older";

        public string DisplayName => "Older";

        public string Description => "";

        public string? Icon => null;

        public Control CreateView(IMeowsHost host) => throw new NotSupportedException();
    }

    [Fact]
    public void A_plugin_shows_under_all_and_under_every_topic_it_names()
    {
        var nest = PluginTopics.Files | PluginTopics.Gaming;

        Assert.True(PluginFilter.Shows(nest, null));
        Assert.True(PluginFilter.Shows(nest, PluginTopics.Files));
        Assert.True(PluginFilter.Shows(nest, PluginTopics.Gaming));
        Assert.False(PluginFilter.Shows(nest, PluginTopics.Tabletop));

        // Said nothing: All only.
        Assert.True(PluginFilter.Shows(PluginTopics.None, null));
        Assert.False(PluginFilter.Shows(PluginTopics.None, PluginTopics.Files));
    }

    [Fact]
    public void The_chips_are_all_first_then_only_topics_somebody_has_with_their_counts()
    {
        var chips = PluginFilter.Chips(
        [
            PluginTopics.Files,
            PluginTopics.Files | PluginTopics.Gaming,
            PluginTopics.Tabletop,
            PluginTopics.None,
        ]);

        Assert.Equal(
            [((PluginTopics?)null, 4), (PluginTopics.Files, 2), (PluginTopics.Gaming, 1), (PluginTopics.Tabletop, 1)],
            chips);
    }

    [Fact]
    public void Nothing_installed_is_one_chip_saying_all_nothing()
    {
        Assert.Equal([((PluginTopics?)null, 0)], PluginFilter.Chips([]));
    }

    [Theory]
    [InlineData("Gaming", PluginTopics.Gaming)]
    [InlineData("tabletop", PluginTopics.Tabletop)]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("Snacks", null)]
    [InlineData("None", null)]
    [InlineData("Files, Gaming", null)]
    public void A_kept_filter_reads_back_as_its_topic_or_as_all(string? kept, PluginTopics? expected)
    {
        Assert.Equal(expected, PluginFilter.Parse(kept));
    }

    [Fact]
    public void The_descriptor_carries_what_the_plugin_said_and_an_older_plugin_says_nothing()
    {
        var about = PluginDescriptor.Loaded(new About(PluginTopics.Social | PluginTopics.Gaming), @"C:\plugins\about\about.dll");
        var older = PluginDescriptor.Loaded(new Older(), @"C:\plugins\older\older.dll");

        Assert.Equal(PluginTopics.Social | PluginTopics.Gaming, about.Topics);
        Assert.Equal(PluginTopics.None, older.Topics);
    }

    [Fact]
    public void Picking_a_chip_asks_for_its_topic_and_unpicking_the_lit_one_does_nothing()
    {
        var picked = new List<PluginTopics?>();
        var gaming = new PluginFilterViewModel(PluginTopics.Gaming, 3, isSelected: false, picked.Add);
        var all = new PluginFilterViewModel(null, 9, isSelected: true, picked.Add);

        gaming.IsSelected = true;
        all.IsSelected = false;

        Assert.Equal([(PluginTopics?)PluginTopics.Gaming], picked);
        Assert.True(all.IsSelected);
        Assert.Equal("Gaming 3", gaming.Text);
        Assert.Equal("All 9", all.Text);
    }

    public static TheoryData<string> EveryPlugin()
    {
        var data = new TheoryData<string>();
        foreach (var type in ShippedPlugins.Types)
            data.Add(type.FullName!);
        return data;
    }

    /// <summary>A plugin that ships here is under some filter besides All; Kitten picks one from the heading.</summary>
    [Theory]
    [MemberData(nameof(EveryPlugin))]
    public void Every_plugin_that_ships_names_a_topic(string typeName)
    {
        var plugin = (IMeowsPlugin)Activator.CreateInstance(ShippedPlugins.Types.Single(t => t.FullName == typeName))!;
        Assert.NotEqual(PluginTopics.None, plugin.Topics);
    }
}
