using Avalonia.Controls;
using Mews.Plugins;
using Mews.Plugins.Abstractions;
using Mews.ViewModels;

namespace Mews.Tests;

public sealed class PluginGroupingTests
{
    /// <summary>
    /// A plugin that says nothing about a group. Written against the contract as it was before
    /// Category existed, which is the case that has to keep working.
    /// </summary>
    private sealed class Plain(string name) : IMewsPlugin
    {
        public string Id => "test." + name.ToLowerInvariant();

        public string DisplayName => name;

        public string Description => "";

        public string? Icon => null;

        public Control CreateView(IMewsHost host) => throw new NotSupportedException();
    }

    private sealed class Grouped(string name, string category) : IMewsPlugin
    {
        public string Id => "test." + name.ToLowerInvariant();

        public string DisplayName => name;

        public string Description => "";

        public string? Icon => null;

        public string Category => category;

        public Control CreateView(IMewsHost host) => throw new NotSupportedException();
    }

    private static PluginEntryViewModel Entry(IMewsPlugin plugin) =>
        new(PluginDescriptor.Loaded(plugin, $@"C:\plugins\{plugin.Id}\{plugin.Id}.dll"), (_, _) => { });

    [Fact]
    public void A_plugin_that_names_no_group_still_appears()
    {
        // The default on the interface is what keeps a plugin built against 0.1.0 working, so
        // this is the case that must not fall through a crack.
        var groups = PluginGroupViewModel.Arrange([Entry(new Plain("Older"))]);

        var only = Assert.Single(groups);
        Assert.Equal(PluginGroupViewModel.Ungrouped, only.Name);
        Assert.Equal("Older", Assert.Single(only.Entries).DisplayName);
    }

    [Fact]
    public void Plugins_naming_the_same_group_end_up_together()
    {
        var groups = PluginGroupViewModel.Arrange([
            Entry(new Grouped("Kibble", "Posting bot")),
            Entry(new Grouped("Telegram Poster", "Posting bot")),
            Entry(new Grouped("Saucer", "Everyday")),
        ]);

        Assert.Equal(2, groups.Count);
        var posting = groups.Single(g => g.Name == "Posting bot");
        Assert.Equal(["Kibble", "Telegram Poster"], posting.Entries.Select(e => e.DisplayName));
        // Saucer is general purpose, so it deliberately does not sit with the bot plugins.
        Assert.Equal("Saucer", Assert.Single(groups.Single(g => g.Name == "Everyday").Entries).DisplayName);
    }

    [Fact]
    public void Groups_are_alphabetical_and_the_ungrouped_one_is_always_last()
    {
        var groups = PluginGroupViewModel.Arrange([
            Entry(new Plain("Stray")),
            Entry(new Grouped("Zebra", "Zzz last alphabetically")),
            Entry(new Grouped("Alpha", "Aaa first alphabetically")),
        ]);

        Assert.Equal(
            ["Aaa first alphabetically", "Zzz last alphabetically", PluginGroupViewModel.Ungrouped],
            groups.Select(g => g.Name));
    }

    [Fact]
    public void Spelling_a_group_with_different_capitals_does_not_split_it()
    {
        var groups = PluginGroupViewModel.Arrange([
            Entry(new Grouped("One", "Posting bot")),
            Entry(new Grouped("Two", "POSTING BOT")),
        ]);

        Assert.Single(groups);
        Assert.Equal(2, groups[0].Entries.Count);
    }

    [Fact]
    public void A_blank_group_counts_as_not_having_said()
    {
        // Whitespace is a typo, not a group name, and a heading made of spaces would be baffling.
        var groups = PluginGroupViewModel.Arrange([Entry(new Grouped("Careless", "   "))]);

        Assert.Equal(PluginGroupViewModel.Ungrouped, Assert.Single(groups).Name);
    }

    [Fact]
    public void The_count_beside_a_heading_reads_properly_for_one()
    {
        var groups = PluginGroupViewModel.Arrange([
            Entry(new Grouped("Alone", "Solo")),
            Entry(new Grouped("Two", "Pair")),
            Entry(new Grouped("Three", "Pair")),
        ]);

        Assert.Equal("1 plugin", groups.Single(g => g.Name == "Solo").CountText);
        Assert.Equal("2 plugins", groups.Single(g => g.Name == "Pair").CountText);
    }

    [Fact]
    public void Nothing_at_all_produces_no_headings()
    {
        Assert.Empty(PluginGroupViewModel.Arrange([]));
    }
}
