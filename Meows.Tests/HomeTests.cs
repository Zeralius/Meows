using System.Collections.ObjectModel;
using Avalonia.Controls;
using Meows.Plugins;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Basket.Services;
using Meows.Services;
using Meows.ViewModels;

namespace Meows.Tests;

/// <summary>
/// Home 2.0: cards in the person's order, hidden cards listed to come back, what the rules did
/// overnight, and up to three lines from plugins that say more.
/// </summary>
public sealed class HomeCardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "meows-home-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class Quiet(string name) : IMeowsPlugin
    {
        public string Id => "test." + name.ToLowerInvariant();

        public string DisplayName => name;

        public string Description => "";

        public string? Icon => null;

        public Control CreateView(IMeowsHost host) => throw new NotSupportedException();
    }

    private static PluginEntryViewModel Entry(string name) =>
        new(PluginDescriptor.Loaded(new Quiet(name), $@"C:\plugins\{name}\{name}.dll"), (_, _) => { });

    private static HomeViewModel Home(
        ObservableCollection<PluginEntryViewModel> plugins,
        MeowsStore? store = null,
        Func<DateTime?>? lastSeen = null,
        IList<string>? order = null,
        IList<string>? hidden = null,
        Action? saved = null,
        Func<PluginEntryViewModel, IReadOnlyList<Glance>?>? glances = null)
    {
        var notifications = new NotificationCenter();
        var background = new BackgroundTaskService(notifications, new ShellLog());
        var saves = 0;
        return new HomeViewModel(plugins, _ => true, _ => null, _ => { }, notifications, background,
            store, id => id, lastSeen ?? (() => null), _ => { }, glances,
            order ?? [], hidden ?? [], () => { saves++; saved?.Invoke(); });
    }

    [Fact]
    public void Cards_keep_catalog_order_until_moved_and_moves_are_written_down()
    {
        var plugins = new ObservableCollection<PluginEntryViewModel> { Entry("Alpha"), Entry("Beta"), Entry("Gamma") };
        var order = new List<string>();
        var saves = 0;
        using var home = Home(plugins, order: order, saved: () => saves++);

        home.Refresh();
        Assert.Equal(["Alpha", "Beta", "Gamma"], home.Plugins.Select(p => p.Name));

        var beta = home.Plugins.Single(p => p.Name == "Beta");
        Assert.True(beta.CanMoveUp);
        Assert.True(beta.CanMoveDown);
        beta.MoveDownCommand.Execute(null);

        Assert.Equal(["Alpha", "Gamma", "Beta"], home.Plugins.Select(p => p.Name));
        Assert.Equal(["test.alpha", "test.gamma", "test.beta"], order);
        Assert.True(saves > 0);

        // The top cannot move up and the bottom cannot move down.
        Assert.False(home.Plugins[0].CanMoveUp);
        Assert.False(home.Plugins[^1].CanMoveDown);

        // A plugin that arrives later, knowing no order, lands at the end.
        plugins.Add(Entry("Delta"));
        home.Refresh();
        Assert.Equal(["Alpha", "Gamma", "Beta", "Delta"], home.Plugins.Select(p => p.Name));
    }

    [Fact]
    public void Hiding_lists_the_card_to_come_back_and_reset_restores_everything()
    {
        var plugins = new ObservableCollection<PluginEntryViewModel> { Entry("Alpha"), Entry("Beta") };
        var order = new List<string>();
        var hidden = new List<string>();
        using var home = Home(plugins, order: order, hidden: hidden);

        home.Refresh();
        home.Plugins.Single(p => p.Name == "Beta").HideCommand.Execute(null);

        Assert.Equal(["Alpha"], home.Plugins.Select(p => p.Name));
        Assert.Equal(["Beta"], home.HiddenPlugins.Select(p => p.Name));
        Assert.Contains("test.beta", hidden);

        home.HiddenPlugins.Single(p => p.Name == "Beta").ShowCommand.Execute(null);
        Assert.Equal(["Alpha", "Beta"], home.Plugins.Select(p => p.Name));
        Assert.Empty(home.HiddenPlugins);

        // Hidden while reordered: reset puts every card back where it was.
        home.Plugins.Single(p => p.Name == "Beta").MoveUpCommand.Execute(null);
        home.Plugins.Single(p => p.Name == "Alpha").HideCommand.Execute(null);
        home.ResetOrderCommand.Execute(null);
        Assert.Equal(["Alpha", "Beta"], home.Plugins.Select(p => p.Name));
        Assert.Empty(order);
        Assert.Empty(hidden);
    }

    [Fact]
    public void Overnight_names_what_the_rules_did_while_hidden_and_nothing_otherwise()
    {
        var store = new MeowsStore(Path.Combine(_root, "store"), _ => { });
        store.For("meows.instinct").Record("fired", "Water the plants", "Basket: added to Basket");
        store.For("meows.instinct").Record("declined", "Nothing", "nothing to do");
        store.For("meows.collar").Record("handled", "TÜV");

        var plugins = new ObservableCollection<PluginEntryViewModel> { Entry("Alpha") };
        using var home = Home(plugins, store, lastSeen: () => DateTime.UtcNow.AddHours(-8));

        home.Refresh();

        Assert.True(home.HasOvernight);
        Assert.Equal(2, home.Overnight.Count);
        Assert.All(home.Overnight, l => Assert.Equal("meows.instinct", l.Plugin));

        using var never = Home(plugins, store, lastSeen: () => null);
        never.Refresh();
        Assert.False(never.HasOvernight);
    }

    [Fact]
    public void A_plugin_that_says_more_gets_three_lines_and_one_that_says_nothing_keeps_its_card()
    {
        var plugins = new ObservableCollection<PluginEntryViewModel> { Entry("Talks"), Entry("Silent") };
        using var home = Home(plugins, glances: p => p.DisplayName == "Talks"
            ? [new Glance("2 overdue"), new Glance("Milk", IsTrouble: true), new Glance("Eggs", IsTrouble: true), new Glance("Too many")]
            : null);

        home.Refresh();

        var talks = home.Plugins.Single(l => l.Name == "Talks");
        Assert.Equal("2 overdue", talks.GlanceText);
        Assert.Equal(["Milk", "Eggs"], talks.ExtraGlances.Select(g => g.Text));
        Assert.All(talks.ExtraGlances, g => Assert.True(g.IsTrouble));

        // An empty multi-line answer falls back to the single line, like no answer at all.
        using var empty = Home(plugins, glances: _ => []);
        empty.Refresh();
        Assert.All(empty.Plugins, l => Assert.False(l.HasGlance));
    }

    [Fact]
    public void Basket_says_its_summary_and_its_most_overdue_cards()
    {
        var host = new FakeHost(Path.Combine(_root, "basket"));
        host.SaveSettings(new Meows.Plugins.Basket.ViewModels.BasketSettings
        {
            Lists =
            [
                new BasketList
                {
                    Title = "To do",
                    Cards =
                    [
                        new BasketCard { Title = "Milk", Due = DateTime.Today.AddDays(-3) },
                        new BasketCard { Title = "Eggs", Due = DateTime.Today.AddDays(-1) },
                        new BasketCard { Title = "Bread", Due = DateTime.Today.AddDays(-2) },
                    ],
                },
            ],
        });
        using var model = new Meows.Plugins.Basket.ViewModels.BasketViewModel(host);

        var lines = model.Glances();

        Assert.True(lines.Count <= 3);
        Assert.Equal("3 overdue", lines[0].Text);
        Assert.True(lines[0].IsTrouble);
        Assert.Equal(["Milk", "Bread"], lines.Skip(1).Select(g => g.Text));
    }
}
