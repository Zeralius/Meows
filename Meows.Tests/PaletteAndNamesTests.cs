using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.ViewModels;

namespace Meows.Tests;

/// <summary>The palette's ranking, which is the whole of its cleverness.</summary>
public class PaletteTests
{
    private static PaletteItem Item(string title, string subtitle = "", int weight = 0) =>
        new("·", title, subtitle, () => { }, weight);

    [Fact]
    public void Every_word_has_to_appear_somewhere_in_any_order()
    {
        var items = new[] { Item("Open Purrge", "Duplicates · finds identical files"), Item("Open Chonk", "Disk usage") };

        Assert.Equal(["Open Purrge"], CommandPaletteViewModel.Rank(items, "dup open").Select(i => i.Title));
        Assert.Empty(CommandPaletteViewModel.Rank(items, "purrge disk"));
    }

    [Fact]
    public void A_title_that_starts_with_the_first_word_comes_first_then_weight_then_name()
    {
        var items = new[]
        {
            Item("Zebra light", weight: 5),
            Item("Light theme", weight: 0),
            Item("Dark theme", "light on dark", weight: 9),
        };

        var ranked = CommandPaletteViewModel.Rank(items, "light").Select(i => i.Title).ToList();

        Assert.Equal(["Light theme", "Dark theme", "Zebra light"], ranked);
    }

    [Fact]
    public void With_nothing_typed_everything_is_offered_heaviest_first()
    {
        var items = new[] { Item("b", weight: 1), Item("a", weight: 1), Item("c", weight: 2) };

        Assert.Equal(["c", "a", "b"], CommandPaletteViewModel.Rank(items, "").Select(i => i.Title));
    }

    [Fact]
    public void Opening_builds_the_list_and_selects_the_first_and_enter_runs_it()
    {
        var ran = new List<string>();
        var palette = new CommandPaletteViewModel(
            () => [Item("Open Purrge"), new PaletteItem("·", "Open Chonk", "", () => ran.Add("chonk"))],
            q => [new PaletteItem("·", "history " + q, "", () => ran.Add("history"), weight: -1)]);

        palette.Open();
        Assert.Equal(2, palette.Items.Count);
        Assert.Equal("Open Chonk", palette.Selected?.Title);

        palette.Query = "chonk";
        Assert.Equal(["Open Chonk", "history chonk"], palette.Items.Select(i => i.Title));

        palette.MoveSelection(+1);
        palette.RunSelected();

        Assert.Equal(["history"], ran);
        Assert.False(palette.IsOpen);
    }
}

/// <summary>Feline or plain, and the identity underneath.</summary>
public class PluginNamesTests : IDisposable
{
    private sealed class Sample : IMeowsPlugin
    {
        public string Id => "meows.sample";

        public string DisplayName => "Whiskers";

        public string PlainName => "sample.name.plain";

        public string Description => "";

        public string? Icon => null;

        public Control CreateView(IMeowsHost host) => new Panel();
    }

    private sealed class Unnamed : IMeowsPlugin
    {
        public string Id => "meows.unnamed";

        public string DisplayName => "Just Me";

        public string Description => "";

        public string? Icon => null;

        public Control CreateView(IMeowsHost host) => new Panel();
    }

    public void Dispose() => PluginNames.Feline = true;

    [Fact]
    public void The_switch_picks_the_name_and_the_key_is_looked_up()
    {
        var plugin = new Sample();
        PluginNames.Register(plugin);

        PluginNames.Feline = true;
        Assert.Equal("Whiskers", PluginNames.For(plugin));
        Assert.Equal("Whiskers", PluginNames.Display("Whiskers"));

        PluginNames.Feline = false;
        // No catalogue carries sample.name.plain, so the key shows, which is the rule everywhere.
        Assert.Equal("sample.name.plain", PluginNames.For(plugin));
        Assert.Equal("sample.name.plain", PluginNames.Display("Whiskers"));
        Assert.Equal("Whiskers", PluginNames.Other(plugin));
    }

    [Fact]
    public void A_plugin_that_never_said_reads_the_same_either_way()
    {
        var plugin = new Unnamed();
        PluginNames.Register(plugin);

        PluginNames.Feline = false;
        Assert.Equal("Just Me", PluginNames.For(plugin));
        Assert.Equal("Just Me", PluginNames.Display("Just Me"));
    }

    [Fact]
    public void Flipping_the_switch_says_so_once()
    {
        var heard = 0;
        Action listener = () => heard++;
        PluginNames.Changed += listener;
        try
        {
            PluginNames.Feline = false;
            PluginNames.Feline = false;
            PluginNames.Feline = true;
        }
        finally
        {
            PluginNames.Changed -= listener;
        }

        Assert.Equal(2, heard);
    }

    [Fact]
    public void Every_shipped_plugin_has_a_plain_name_in_both_languages()
    {
        var text = TestStrings.Load();
        foreach (var type in ShippedPlugins.Types)
        {
            var plugin = (IMeowsPlugin)Activator.CreateInstance(type)!;

            // Telegram Poster is named after the thing it controls, not after the cat, so it
            // has no second name to give. Everything named after the cat has to.
            if (plugin.PlainName == plugin.DisplayName)
                continue;

            text.Use("en");
            Assert.NotEqual(plugin.PlainName, text[plugin.PlainName]);
            text.Use("de");
            Assert.NotEqual(plugin.PlainName, text[plugin.PlainName]);
        }
    }
}
