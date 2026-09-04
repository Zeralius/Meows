using Meows.Plugins.Birdwatch.ViewModels;

namespace Meows.Tests;

/// <summary>
/// Looking again on its own.
///
/// The interval is fixed when the work is handed over, so every part of this is about asking for
/// the work again at the right moments: when the box is ticked, when the interval changes, and
/// when the last account goes away and there is nothing left to look at.
///
/// What a pass actually does once it starts is not here. It hops onto the UI thread, and this
/// suite runs without a dispatcher, so it is checked in the running window instead.
/// </summary>
public class BirdwatchAutoTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "meows-auto-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private (FakeHost Host, BirdwatchViewModel Model) Watching(BirdwatchSettings settings)
    {
        var host = new FakeHost(_root);
        settings.IntakeFolder = Path.Combine(_root, "intake");
        host.SaveSettings(settings);

        return (host, new BirdwatchViewModel(host, new FakeFeed(), new System.Net.Http.HttpClient()));
    }

    private static BirdwatchSettings OneAccount() =>
        new() { Handles = ["someone.bsky.social"] };

    [Fact]
    public void Nothing_is_scheduled_until_it_is_asked_for()
    {
        var (host, model) = Watching(OneAccount());
        using var _ = model;

        Assert.Empty(host.Work.Scheduled);
    }

    [Fact]
    public void Ticking_the_box_asks_for_the_chosen_interval()
    {
        var (host, model) = Watching(OneAccount());
        using var _ = model;

        model.AutoRefresh = true;

        var scheduled = Assert.Single(host.Work.Scheduled);
        Assert.Equal(TimeSpan.FromMinutes(15), scheduled.Interval);

        // Turning it on means from now on. Running the moment the box is ticked would fetch
        // whatever the Refresh button right beside it was about to fetch anyway.
        Assert.False(scheduled.RunImmediately);
    }

    [Fact]
    public void Changing_the_interval_asks_again_with_the_new_one()
    {
        var (host, model) = Watching(OneAccount());
        using var _ = model;

        model.AutoRefresh = true;
        var first = host.Work.Scheduled[^1];

        model.SelectedRefresh = model.RefreshOptions.Single(o => o.Minutes == 60);

        Assert.Equal(2, host.Work.Scheduled.Count);
        Assert.Equal(TimeSpan.FromMinutes(60), host.Work.Scheduled[^1].Interval);

        // The old one has to go, or both keep firing and the interval only ever gets shorter.
        Assert.True(first.Task.Cancelled);
    }

    [Fact]
    public void Unticking_the_box_calls_it_off()
    {
        var (host, model) = Watching(OneAccount());
        using var _ = model;

        model.AutoRefresh = true;
        var scheduled = host.Work.Scheduled[^1];

        model.AutoRefresh = false;

        Assert.True(scheduled.Task.Cancelled);
        Assert.Single(host.Work.Scheduled);
    }

    [Fact]
    public void Closing_the_tab_calls_it_off()
    {
        var (host, model) = Watching(OneAccount());

        model.AutoRefresh = true;
        var scheduled = host.Work.Scheduled[^1];

        model.Dispose();

        Assert.True(scheduled.Task.Cancelled);
    }

    [Fact]
    public void The_choice_is_remembered()
    {
        var host = new FakeHost(_root);
        host.SaveSettings(new BirdwatchSettings
        {
            Handles = ["someone.bsky.social"],
            IntakeFolder = Path.Combine(_root, "intake"),
        });

        using (var first = new BirdwatchViewModel(host, new FakeFeed(), new System.Net.Http.HttpClient()))
        {
            first.AutoRefresh = true;
            first.SelectedRefresh = first.RefreshOptions.Single(o => o.Minutes == 180);
        }

        // A fresh tab off the same saved settings, which is what reopening the plugin does.
        using var again = new BirdwatchViewModel(host, new FakeFeed(), new System.Net.Http.HttpClient());

        Assert.True(again.AutoRefresh);
        Assert.Equal(180, again.SelectedRefresh.Minutes);

        // And it starts looking again by itself rather than waiting to be ticked a second time.
        Assert.Equal(TimeSpan.FromMinutes(180), host.Work.Scheduled[^1].Interval);
    }

    [Fact]
    public void With_nothing_watched_there_is_nothing_to_ask_for()
    {
        var (host, model) = Watching(new BirdwatchSettings { AutoRefresh = true });
        using var _ = model;

        Assert.Empty(host.Work.Scheduled);
    }

    [Fact]
    public void Adding_the_first_account_starts_it()
    {
        var (host, model) = Watching(new BirdwatchSettings { AutoRefresh = true, RefreshEveryMinutes = 5 });
        using var _ = model;

        model.NewHandle = "https://bsky.app/profile/zeralius.bsky.social";
        model.AddHandleCommand.Execute(null);

        var scheduled = Assert.Single(host.Work.Scheduled);
        Assert.Equal(TimeSpan.FromMinutes(5), scheduled.Interval);
    }

    [Fact]
    public void Removing_the_last_account_stops_it()
    {
        var (host, model) = Watching(OneAccount());
        using var _ = model;

        model.AutoRefresh = true;
        var scheduled = host.Work.Scheduled[^1];

        model.RemoveHandleCommand.Execute(model.Watched[0]);

        Assert.True(scheduled.Task.Cancelled);
        Assert.Single(host.Work.Scheduled);
    }

    [Fact]
    public async Task A_pass_that_has_been_called_off_does_nothing()
    {
        // The shell cancels the work when a plugin is switched off, and a pass already waiting
        // its turn still gets to start. It should notice and go home rather than fetch into a
        // tab nobody is looking at any more.
        var host = new FakeHost(_root);
        host.SaveSettings(new BirdwatchSettings
        {
            Handles = ["someone.bsky.social"],
            IntakeFolder = Path.Combine(_root, "intake"),
        });

        var feed = new FakeFeed();
        using var model = new BirdwatchViewModel(host, feed, new System.Net.Http.HttpClient());
        model.AutoRefresh = true;

        await host.Work.RunLatestCalledOffAsync();

        Assert.Empty(feed.Asked);
    }

    [Fact]
    public void Every_interval_offered_reads_in_both_languages()
    {
        // The dropdown is built from keys, and a key with no string behind it shows up as the
        // key itself, which is the sort of thing that reaches a screenshot before it reaches a
        // bug report.
        var (host, model) = Watching(OneAccount());
        using var _ = model;

        foreach (var option in model.RefreshOptions)
        {
            Assert.NotEqual("", option.Label.Value);
            Assert.DoesNotContain("birdwatch.every.", option.Label.Value, StringComparison.Ordinal);
        }

        // Fifteen is the default, so it has to be one of the ones on offer or the dropdown opens
        // with nothing selected.
        Assert.Equal(15, model.SelectedRefresh.Minutes);
    }
}
