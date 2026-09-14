using Meows.Plugins.Abstractions;
using Meows.ViewModels;

namespace Meows.Tests;

/// <summary>The one line on a plugin's card that says what it last did and what it is watching.</summary>
public sealed class PluginHealthTests
{
    private static WatchInfo Watch(string plugin, bool stopped) =>
        new(plugin, plugin, "w", TimeSpan.FromMinutes(5), DateTime.Now, null, null, 0, false,
            stopped ? DateTime.Now : null, stopped ? "boom" : null, "");

    [Fact]
    public void Nothing_recorded_and_nothing_watched_is_no_line_at_all()
    {
        var health = PluginHealth.Describe(null, []);

        Assert.Equal("", health.Text);
        Assert.Equal(0, health.Watches);
    }

    [Fact]
    public void The_last_line_and_the_watches_read_as_one_sentence()
    {
        var last = new StoredEvent(1, DateTime.Now.AddMinutes(-3), "meows.kibble", "sent", @"C:\x\a.png", "queued for Alpha",
            new Dictionary<string, string>());

        var health = PluginHealth.Describe(last, [Watch("meows.kibble", false), Watch("meows.kibble", true)]);

        Assert.Equal("Last: a.png, queued for Alpha, 3 min ago · 1 watch running 1 stopped.", health.Text);
        Assert.Equal(2, health.Watches);
        Assert.Equal(1, health.StoppedWatches);
    }

    [Fact]
    public void A_line_without_a_detail_falls_back_to_its_kind()
    {
        var last = new StoredEvent(1, DateTime.Now.AddDays(-2), "meows.purrge", "recycled", @"C:\x\b.png", null,
            new Dictionary<string, string>());

        Assert.Equal("Last: b.png, recycled, 2 days ago", PluginHealth.Describe(last, []).Text);
    }
}
