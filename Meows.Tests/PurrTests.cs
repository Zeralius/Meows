using Meows.Plugins.Abstractions;
using Meows.Plugins.Purr.ViewModels;

namespace Meows.Tests;

/// <summary>
/// Purr reads the shell's list of watches and says it in words. It owns nothing, so the tests
/// hand it lists and read the words back.
/// </summary>
public sealed class PurrTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "purr-" + Guid.NewGuid().ToString("N")[..10]);

    public PurrTests() => Directory.CreateDirectory(_root);

    private static WatchInfo Watch(string plugin, string title, TimeSpan every, DateTime? last = null, int passes = 0,
        bool busy = false, DateTime? stopped = null, string? failure = null, string status = "") =>
        new(plugin.ToLowerInvariant(), plugin, title, every, DateTime.Now.AddHours(-1), last,
            stopped is null && !busy ? (last ?? DateTime.Now.AddHours(-1)) + every : null,
            passes, busy, stopped, failure, status);

    private (PurrViewModel Model, FakeHost Host) Open(params WatchInfo[] watches)
    {
        var host = new FakeHost(Path.Combine(_root, "hostdata"));
        host.Watches.Items.AddRange(watches);
        return (new PurrViewModel(host), host);
    }

    [Fact]
    public void Nothing_watched_says_so_rather_than_showing_an_empty_list()
    {
        var (model, _) = Open();

        Assert.True(model.IsEmpty);
        Assert.Equal("Nothing is being watched", model.SummaryText);
        Assert.False(model.HasStopped);
    }

    [Fact]
    public void The_summary_counts_watches_and_plugins_and_the_stopped_ones()
    {
        var (model, _) = Open(
            Watch("Collar", "Dates", TimeSpan.FromHours(1), last: DateTime.Now.AddMinutes(-3), passes: 4),
            Watch("Birdwatch", "Refresh", TimeSpan.FromMinutes(30), last: DateTime.Now.AddMinutes(-10), passes: 2),
            Watch("Birdwatch", "Save", TimeSpan.FromMinutes(5), stopped: DateTime.Now.AddMinutes(-1), failure: "no network"));

        Assert.Equal("3 watches across 2 plugins 1 stopped.", model.SummaryText);
        Assert.True(model.HasStopped);
        Assert.Equal(3, model.Watches.Count);
    }

    [Fact]
    public void A_stopped_watch_comes_first_and_says_why()
    {
        var (model, _) = Open(
            Watch("Collar", "Dates", TimeSpan.FromHours(1), last: DateTime.Now.AddMinutes(-3), passes: 4),
            Watch("Saucer", "Clipboard", TimeSpan.FromSeconds(1), stopped: DateTime.Now.AddMinutes(-2), failure: "clipboard locked"));

        var first = model.Watches[0];
        Assert.Equal("Clipboard", first.Title);
        Assert.True(first.IsStopped);
        Assert.Contains("clipboard locked", first.NextText);
        Assert.Contains("stopped", first.NextText);
        Assert.Equal("✕", first.Glyph);
    }

    [Fact]
    public void A_healthy_watch_says_when_it_looked_and_when_it_will_again()
    {
        var (model, _) = Open(Watch("Collar", "Dates", TimeSpan.FromHours(1), last: DateTime.Now.AddMinutes(-3), passes: 4));

        var watch = model.Watches[0];
        Assert.Equal("looked 3 min ago", watch.LastText);
        Assert.StartsWith("next look in", watch.NextText);
        Assert.Equal("every 1 h", watch.EveryText);
        Assert.Equal("looked 4 times so far", watch.PassesText);
        Assert.Equal("●", watch.Glyph);
    }

    [Fact]
    public void A_pass_in_progress_says_what_it_reported()
    {
        var (model, _) = Open(Watch("Birdwatch", "Refresh", TimeSpan.FromMinutes(30), busy: true, status: "2 of 5 accounts"));

        Assert.Equal("looking now: 2 of 5 accounts", model.Watches[0].NextText);
        Assert.Equal("not looked yet", model.Watches[0].LastText);
    }

    [Fact]
    public void The_shell_saying_something_changed_rereads_the_list_and_keeps_the_selection()
    {
        var (model, host) = Open(Watch("Collar", "Dates", TimeSpan.FromHours(1)));
        model.Selected = model.Watches[0];

        host.Watches.Items.Add(Watch("Birdwatch", "Refresh", TimeSpan.FromMinutes(30)));
        host.Watches.Raise();

        Assert.Equal(2, model.Watches.Count);
        Assert.Equal("Dates", model.Selected?.Title);
    }

    [Fact]
    public void Purr_keeps_the_times_fresh_with_a_watch_of_its_own()
    {
        var (_, host) = Open();

        // On the list too, which is as it should be: the watcher is watched.
        Assert.Contains(host.Work.Scheduled, w => w.Title == "Keeping the times fresh");
    }

    [Fact]
    public void Search_finds_a_watch_by_title_or_plugin_and_selects_it()
    {
        var (model, _) = Open(
            Watch("Collar", "Dates", TimeSpan.FromHours(1)),
            Watch("Birdwatch", "Refresh", TimeSpan.FromMinutes(30)));

        var hit = Assert.Single(model.Search("bird", 5));
        hit.Open();

        Assert.Equal("Refresh", model.Selected?.Title);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
        }
    }
}
