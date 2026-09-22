using System.Collections.ObjectModel;
using Avalonia.Controls;
using Meows.Plugins;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Collar.Services;
using Meows.Plugins.Collar.ViewModels;
using Meows.Services;
using Meows.ViewModels;

namespace Meows.Tests;

/// <summary>
/// Contract 1.1.0: a plugin's view model can put a line of its own on its Home card through
/// <see cref="IGlanceable"/>. The shell's line, what it last did and what it is watching, stays
/// under it; a plugin that says nothing keeps the card it had.
/// </summary>
public sealed class GlanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "meows-glance-" + Guid.NewGuid().ToString("N")[..8]);

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

    private static HomeViewModel Home(ObservableCollection<PluginEntryViewModel> plugins, Func<PluginEntryViewModel, Glance?> glance)
    {
        var notifications = new NotificationCenter();
        var background = new BackgroundTaskService(notifications, new ShellLog());
        return new HomeViewModel(plugins, _ => true, glance, _ => { }, notifications, background, null, id => id, () => null, _ => { });
    }

    [Fact]
    public void A_plugin_with_a_glance_has_it_above_the_shells_line_and_one_without_keeps_the_card_it_had()
    {
        var plugins = new ObservableCollection<PluginEntryViewModel> { Entry("Talks"), Entry("Silent") };
        using var home = Home(plugins, p => p.DisplayName == "Talks" ? new Glance("2 queues would fail tonight", IsTrouble: true) : null);

        home.Refresh();

        var talks = home.Plugins.Single(l => l.Name == "Talks");
        Assert.True(talks.HasGlance);
        Assert.Equal("2 queues would fail tonight", talks.GlanceText);
        Assert.True(talks.GlanceIsTrouble);
        Assert.Equal("Nothing to report.", talks.Health);

        var silent = home.Plugins.Single(l => l.Name == "Silent");
        Assert.False(silent.HasGlance);
        Assert.False(silent.GlanceIsTrouble);
        Assert.Equal("Nothing to report.", silent.Health);
    }

    [Fact]
    public void An_empty_glance_is_no_glance()
    {
        var plugins = new ObservableCollection<PluginEntryViewModel> { Entry("Blank") };
        using var home = Home(plugins, _ => new Glance(""));

        home.Refresh();

        Assert.False(Assert.Single(home.Plugins).HasGlance);
    }

    [Fact]
    public void Collar_glances_its_summary_and_goes_red_when_a_date_is_late()
    {
        var host = new FakeHost(_root);
        using var model = new CollarViewModel(host);

        Assert.Null(((IGlanceable)model).Glance());

        model.Add(new CollarEntry { Title = "Domain", Due = DateTime.Today.AddDays(200) });
        var calm = ((IGlanceable)model).Glance();
        Assert.NotNull(calm);
        Assert.Equal(model.Summary, calm.Text);
        Assert.False(calm.IsTrouble);

        model.Add(new CollarEntry { Title = "TÜV", Due = DateTime.Today.AddDays(-2) });
        var late = ((IGlanceable)model).Glance();
        Assert.NotNull(late);
        Assert.True(late.IsTrouble);
    }
}
