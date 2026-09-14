using Meows.Services;
using Meows.ViewModels;

namespace Meows.Tests;

/// <summary>
/// The journal never deletes a line on its own unless a rule says so, and these are the only
/// things that make the file smaller: forgetting, which asks first, the keeper, which applies the
/// rule from the Settings tab, and compacting, which gives the room back.
/// </summary>
public sealed class StoreMaintenanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "store-maint-" + Guid.NewGuid().ToString("N")[..10]);

    private MeowsStore Open() => new(_root, _ => { });

    private static HistoryViewModel.ForgetAge Age(int days) => HistoryViewModel.ForgetAges.Single(a => a.Days == days);

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
        history.SelectedForgetAge = Age(365);
        history.AskToForgetCommand.Execute(null);
        Assert.False(history.IsAskingToForget);
        Assert.Equal("Nothing older than that.", history.Notice);

        // "Everything" catches it; the prompt counts it and nothing has gone yet.
        history.SelectedForgetAge = Age(0);
        history.AskToForgetCommand.Execute(null);
        Assert.True(history.IsAskingToForget);
        Assert.StartsWith("Forget all 1 lines from every plugin", history.ForgetPrompt);
        Assert.Equal(1, store.Count());

        history.CancelForgetCommand.Execute(null);
        Assert.False(history.IsAskingToForget);
        Assert.Equal(1, store.Count());

        history.AskToForgetCommand.Execute(null);
        history.ForgetCommand.Execute(null);
        Assert.False(history.IsAskingToForget);
        Assert.Equal(0, store.Count());
        Assert.StartsWith("Forgot 1 lines", history.Notice);
        Assert.True(history.IsEmpty);
    }

    [Fact]
    public void Forgetting_with_a_plugin_chosen_leaves_the_others_alone()
    {
        var store = Open();
        store.For("meows.kibble").Record("sent", @"C:\x\a.png", "queued");
        store.For("meows.purrge").Record("recycled", @"C:\x\b.png", "binned");
        var history = new HistoryViewModel(store, id => id == "meows.purrge" ? "Purrge" : "Kibble");

        history.SelectedPluginChoice = "Purrge";
        history.SelectedForgetAge = Age(0);
        history.AskToForgetCommand.Execute(null);

        Assert.Contains("from Purrge", history.ForgetPrompt);
        history.ForgetCommand.Execute(null);

        Assert.Equal(1, store.Count());
        Assert.Equal("meows.kibble", store.Events(null, null, null, 10).Single().Plugin);
    }

    [Fact]
    public void The_keeper_applies_the_rule_and_forever_means_nothing_happens()
    {
        var store = Open();
        store.For("meows.kibble").Record("sent", @"C:\x\a.png", "queued");
        var log = new ShellLog();
        var notifications = new NotificationCenter();
        using var background = new BackgroundTaskService(notifications, log);
        var days = 0;

        using var keeper = new HistoryKeeper(store, () => days, log, background, TestStrings.Load());

        Assert.Equal(0, keeper.Apply());
        Assert.Equal(1, store.Count());

        // The test cannot wait thirty days, so the line is backdated behind the store's back.
        // The daily pass is a day away, so this is the only pass that runs.
        Backdate(store, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        days = 30;
        Assert.Equal(1, keeper.Apply());
        Assert.Equal(0, store.Count());

        // And it is on the shell's list of watches, under Meows, for Purr to show.
        Assert.Contains(background.Watches(), w => w.PluginName == "Meows" && w.Title == "Forgetting old history");
    }

    private static void Backdate(MeowsStore store, DateTime at)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={store.FilePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE events SET at = $at";
        command.Parameters.AddWithValue("$at", at.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
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
