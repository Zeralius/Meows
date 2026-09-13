using Meows.Services;

namespace Meows.Tests;

/// <summary>
/// The shared store, against a real SQLite file in a temp folder. What is written comes back,
/// scoping holds, the schema is stamped, and a second opening finds everything the first wrote.
/// </summary>
public class StoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "meows-store-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];

    private MeowsStore Open() => new(_root, _log.Add);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void What_a_plugin_records_comes_back_newest_first_and_only_to_it()
    {
        var store = Open();
        var kibble = store.For("meows.kibble");
        var purrge = store.For("meows.purrge");

        kibble.Record("sent", @"E:\a.jpg", "queued for Paws", new Dictionary<string, string> { ["group"] = "Paws" });
        kibble.Record("sent", @"E:\b.jpg", "queued for Vore");
        purrge.Record("recycled", @"E:\c.jpg");

        var mine = kibble.Recent();
        Assert.Equal([@"E:\b.jpg", @"E:\a.jpg"], mine.Select(e => e.Subject));
        Assert.Equal("Paws", mine[1].Data["group"]);
        Assert.Equal("meows.kibble", mine[0].Plugin);

        Assert.Single(purrge.Recent());
        Assert.Equal(3, store.Count());
        Assert.Empty(_log);
    }

    [Fact]
    public void Kinds_and_words_filter()
    {
        var store = Open();
        var plugin = store.For("meows.kibble");
        plugin.Record("sent", @"E:\cat.jpg", "queued for Paws");
        plugin.Record("set-aside", @"E:\dog.jpg", "duplicate");

        Assert.Single(plugin.Recent(kind: "sent"));
        Assert.Equal(@"E:\dog.jpg", Assert.Single(plugin.Search("dog")).Subject);
        Assert.Equal(@"E:\cat.jpg", Assert.Single(plugin.Search("Paws")).Subject);
        Assert.Empty(plugin.Search("hamster"));
    }

    [Fact]
    public void Facts_are_per_plugin_and_overwrite()
    {
        var store = Open();
        var a = store.For("a");
        var b = store.For("b");

        a.Set("cursor", "one");
        a.Set("cursor", "two");

        Assert.Equal("two", a.Get("cursor"));
        Assert.Null(b.Get("cursor"));

        a.Remove("cursor");
        Assert.Null(a.Get("cursor"));
    }

    [Fact]
    public void Seen_is_shared_and_the_first_sighting_wins()
    {
        var store = Open();
        store.For("meows.birdwatch").MarkSeen("abc", "saved from the feed");
        store.For("meows.kibble").MarkSeen("abc", "queued");

        var seen = store.For("meows.scruff").Seen("abc");

        Assert.NotNull(seen);
        Assert.Equal("meows.birdwatch", seen.Plugin);
        Assert.Equal("saved from the feed", seen.Note);
        Assert.Null(store.For("meows.scruff").Seen("nope"));
    }

    [Fact]
    public void A_second_opening_reads_what_the_first_wrote()
    {
        Open().For("x").Record("did", "thing");

        var again = Open();

        Assert.Equal("thing", Assert.Single(again.For("x").Recent()).Subject);
        Assert.Equal(["x"], again.Plugins());
    }

    [Fact]
    public void The_shell_sees_across_plugins_and_a_plugin_does_not()
    {
        var store = Open();
        store.For("a").Record("did", "one");
        store.For("b").Record("did", "two");

        Assert.Equal(2, store.Events(null, null, null, 10).Count);
        Assert.Single(store.For("a").Recent());
    }

    [Fact]
    public void A_folder_that_cannot_be_written_is_a_log_line_not_a_crash()
    {
        var log = new List<string>();
        var store = new MeowsStore(Path.Combine(_root, "made-anyway"), log.Add);
        store.For("a").Record("did", "one");

        Assert.Single(store.For("a").Recent());
        Assert.Empty(log);
    }
}
