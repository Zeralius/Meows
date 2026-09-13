using Meows.Services;
using Meows.ViewModels;

namespace Meows.Tests;

/// <summary>
/// The journal never deletes a line on its own, so these are the only two things that make the
/// file smaller: forgetting, which asks first, and compacting, which gives the room back.
/// </summary>
public sealed class StoreMaintenanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "store-maint-" + Guid.NewGuid().ToString("N")[..10]);

    private MeowsStore Open() => new(_root, _ => { });

    [Fact]
    public void Forgetting_takes_only_what_is_older_than_the_cutoff_and_leaves_facts_and_seen_alone()
    {
        var store = Open();
        var kibble = store.For("meows.kibble");
        kibble.Record("sent", @"C:\x\old.png", "queued");
        kibble.Record("sent", @"C:\x\new.png", "queued");
        kibble.Set("fact", "kept");
        kibble.MarkSeen("abc");

        // Everything just written is newer than any cutoff in the past.
        Assert.Equal(0, store.CountOlderThan(DateTime.UtcNow.AddDays(-1)));
        Assert.Equal(0, store.Forget(DateTime.UtcNow.AddDays(-1)));
        Assert.Equal(2, store.Count());

        // And older than a moment from now, which is what the test can reach.
        var all = DateTime.UtcNow.AddMinutes(1);
        Assert.Equal(2, store.CountOlderThan(all));
        Assert.Equal(2, store.Forget(all));
        Assert.Equal(0, store.Count());

        Assert.Equal("kept", kibble.Get("fact"));
        Assert.NotNull(kibble.Seen("abc"));
    }

    [Fact]
    public void Compacting_gives_the_room_back_and_the_size_is_known()
    {
        var store = Open();
        var purrge = store.For("meows.purrge");
        for (var i = 0; i < 2000; i++)
            purrge.Record("recycled", $@"C:\x\{i}.png", new string('d', 500));

        var full = store.FileSize;
        Assert.True(full > 100_000, $"expected a fat file, got {full}");

        store.Forget(DateTime.UtcNow.AddMinutes(1));
        Assert.True(store.Compact());

        Assert.True(store.FileSize < full / 4, $"expected the file to shrink from {full}, got {store.FileSize}");
    }

    [Fact]
    public void The_history_tab_asks_first_and_says_how_many_would_go()
    {
        var store = Open();
        store.For("meows.kibble").Record("sent", @"C:\x\a.png", "queued");
        var history = new HistoryViewModel(store, id => id);

        Assert.Contains("1 lines", history.StoreSizeText);

        // Nothing is older than a year, so nothing is asked.
        history.AskToForgetCommand.Execute(365);
        Assert.False(history.IsAskingToForget);
        Assert.Equal("Nothing older than that.", history.Notice);

        // A cutoff in the future catches everything; the prompt counts it and nothing has gone yet.
        history.AskToForgetCommand.Execute(-1);
        Assert.True(history.IsAskingToForget);
        Assert.StartsWith("Forget 1 lines", history.ForgetPrompt);
        Assert.Equal(1, store.Count());

        history.CancelForgetCommand.Execute(null);
        Assert.False(history.IsAskingToForget);
        Assert.Equal(1, store.Count());

        history.AskToForgetCommand.Execute(-1);
        history.ForgetCommand.Execute(null);
        Assert.False(history.IsAskingToForget);
        Assert.Equal(0, store.Count());
        Assert.StartsWith("Forgot 1 lines", history.Notice);
        Assert.True(history.IsEmpty);
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
