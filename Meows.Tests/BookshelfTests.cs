using Meows.Plugins.Abstractions;
using Meows.Plugins.Bookshelf;
using Meows.Plugins.Bookshelf.Services;
using Meows.Plugins.Bookshelf.ViewModels;

namespace Meows.Tests;

/// <summary>
/// The shelf: names from file names, identical bytes named, unfinished least recently opened
/// first, and staging that copies without moving.
/// </summary>
public class LibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "library-" + Guid.NewGuid().ToString("N")[..10]);

    public LibraryTests() => Directory.CreateDirectory(_root);

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

    private static IMeowsText Text() => TestStrings.Load();

    private string Book(string name, byte fill, int size = 200)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, Enumerable.Repeat(fill, size).ToArray());
        return path;
    }

    [Fact]
    public void Shelved_names_split_into_author_and_title()
    {
        Assert.Equal(("Ursula K. Le Guin", "A Wizard of Earthsea"), Library.ParseName("Ursula K. Le Guin - A Wizard of Earthsea.epub"));
        Assert.Equal(("", "A Manual"), Library.ParseName("A_Manual.pdf"));
        Assert.Equal(("Author", "Title"), Library.ParseName("Author.Title.mobi"));
    }

    [Fact]
    public void Identical_bytes_keep_the_most_recently_opened()
    {
        var states = new List<BookState>();
        var folders = new List<BookFolder> { new() { Path = _root } };
        var old = Book("Author - Old.epub", 7);
        var now = Book("Author - New.epub", 7);
        File.SetLastAccessTimeUtc(old, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastAccessTimeUtc(now, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));

        var books = Library.Scan(folders, states);

        Assert.Equal(2, books.Count);
        var victims = Library.DuplicateVictims(books);
        Assert.Equal(old, Assert.Single(victims).Path);
    }

    [Fact]
    public void Same_size_other_bytes_are_no_copies()
    {
        var states = new List<BookState>();
        Book("Author - A.epub", 1);
        Book("Author - B.epub", 2);

        var books = Library.Scan([new BookFolder { Path = _root }], states);

        Assert.Empty(Library.DuplicateVictims(books));
    }

    [Fact]
    public void Unfinished_sorts_least_recently_opened_first()
    {
        var states = new List<BookState>();
        var fresh = Book("Author - Fresh.epub", 1);
        var stale = Book("Author - Stale.epub", 2);
        File.SetLastAccessTimeUtc(fresh, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastAccessTimeUtc(stale, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        states.Add(new BookState { Path = fresh, Status = BookStatus.Reading });
        states.Add(new BookState { Path = stale, Status = BookStatus.Reading });

        var books = Library.Scan([new BookFolder { Path = _root }], states);

        Assert.Equal([stale, fresh], Library.Unfinished(books).Select(b => b.Path));
    }

    [Fact]
    public void States_for_gone_files_are_forgotten_quietly()
    {
        var states = new List<BookState> { new() { Path = Path.Combine(_root, "gone.epub"), Status = BookStatus.Reading } };
        Book("Author - Here.epub", 3);

        var books = Library.Scan([new BookFolder { Path = _root }], states);

        Assert.Single(books);
        Assert.Empty(states);
    }

    [Fact]
    public void Staging_copies_without_moving_and_leaves_what_is_there()
    {
        var first = Book("Author - First.epub", 4);
        var staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(staging);
        File.WriteAllBytes(Path.Combine(staging, "Author - First.epub"), Enumerable.Repeat((byte)4, 200).ToArray());

        var (copied, skipped) = Library.Stage([first], staging);

        Assert.Equal(0, copied);
        Assert.Equal(1, skipped);
        Assert.True(File.Exists(first));

        var second = Book("Author - Second.epub", 5);
        (copied, skipped) = Library.Stage([second], staging);

        Assert.Equal(1, copied);
        Assert.Equal(0, skipped);
        Assert.True(File.Exists(second));
        Assert.True(File.Exists(Path.Combine(staging, "Author - Second.epub")));
    }

    [Fact]
    public void The_summary_names_books_unfinished_and_what_copies_hold()
    {
        var text = Text();

        Assert.Equal("", Library.SummaryOf(null, text));
        Assert.Null(Library.GlanceOf(null, text));

        var summary = new BookSummary { Books = 8, Unfinished = 3, Duplicates = 2, DuplicateBytes = 2048 };
        Assert.Equal("8 books, 3 unfinished, 2 copies holding 2 KB", Library.SummaryOf(summary, text));
        Assert.True(Library.GlanceOf(summary, text)?.IsTrouble);

        var clear = new BookSummary { Books = 8, Unfinished = 3 };
        Assert.Equal("8 books, 3 unfinished", Library.SummaryOf(clear, text));
        Assert.False(Library.GlanceOf(clear, text)?.IsTrouble);
    }
}

/// <summary>
/// The tab itself: shelf read, statuses kept, unfinished staged, copies recycled through the
/// Bin, and the half that matters with no window.
/// </summary>
public sealed class BookshelfTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bookshelf-" + Guid.NewGuid().ToString("N")[..10]);

    public BookshelfTests()
    {
        Directory.CreateDirectory(_root);
        TestStrings.Install();
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

    private FakeHost Host(string name) => new(Path.Combine(_root, "host-" + name));

    private string Library(params (string Name, byte Fill)[] books)
    {
        var folder = Path.Combine(_root, "library-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        foreach (var (name, fill) in books)
            File.WriteAllBytes(Path.Combine(folder, name), Enumerable.Repeat(fill, 200).ToArray());
        return folder;
    }

    private BookshelfViewModel Model(FakeHost host, string profile, string folder)
    {
        var model = new BookshelfViewModel(host, profile);
        model.AddFolder(folder);
        model.RescanNow();
        return model;
    }

    [Fact]
    public void First_open_looks_for_a_calibre_library_and_says_so()
    {
        var calibre = Path.Combine(_root, "profile", "Calibre Library");
        Directory.CreateDirectory(calibre);
        File.WriteAllBytes(Path.Combine(calibre, "Author - Title.epub"), [1, 2, 3]);
        var host = Host("open");

        using var model = new BookshelfViewModel(host, Path.Combine(_root, "profile"));

        Assert.Contains(model.Folders, f => f.Folder.Path == calibre);
        Assert.False(string.IsNullOrWhiteSpace(model.Status));
        Assert.False(model.HasError);
    }

    [Fact]
    public void Refresh_reports_when_it_ran()
    {
        var host = Host("refresh");

        using var model = new BookshelfViewModel(host, Path.Combine(_root, "nobody"));

        model.RefreshCommand.Execute(null);

        Assert.Equal("Scanning…", model.Status);
    }

    [Fact]
    public void Shown_books_name_authors_and_keep_their_status()
    {
        var host = Host("shown");
        var folder = Library(("Le Guin - Earthsea.epub", 1), ("Manual.pdf", 2));

        using var model = Model(host, Path.Combine(_root, "profile"), folder);
        model.Selected = model.Rows.Single(r => r.Book.Title == "Earthsea");
        model.Selected.Status = BookStatus.Reading;

        Assert.Equal("Le Guin", model.Rows.Single(r => r.Book.Title == "Earthsea").Author);
        Assert.Contains(host.Store.Events, e => e.Kind == "scanned");

        model.Selected.Status = BookStatus.Finished;
        Assert.Contains(host.Store.Events, e => e.Kind == "finished");

        using var again = Model(host, Path.Combine(_root, "profile"), folder);
        Assert.Equal(BookStatus.Finished, again.Rows.Single(r => r.Book.Title == "Earthsea").Book.Status);
    }

    [Fact]
    public void Unfinished_stage_to_the_staging_folder_without_moving()
    {
        var host = Host("stage");
        var folder = Library(("Author - Reading.epub", 3));
        var staging = Path.Combine(_root, "staging");

        using var model = Model(host, Path.Combine(_root, "profile"), folder);
        model.Selected = model.Rows[0];
        model.Selected.Status = BookStatus.Reading;

        // Nowhere to stage to: said out loud, nothing thrown.
        model.StageUnfinishedCommand.Execute(null);

        host.Picks.Answers.Enqueue(staging);
        model.SetStagingCommand.Execute(null);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (model.StagingText != staging && DateTime.UtcNow < deadline)
            Thread.Sleep(50);
        Assert.Equal(staging, model.StagingText);

        model.StageUnfinishedCommand.Execute(null);
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(Path.Combine(staging, "Author - Reading.epub")) && DateTime.UtcNow < deadline)
            Thread.Sleep(50);

        Assert.True(File.Exists(Path.Combine(staging, "Author - Reading.epub")));
        Assert.True(File.Exists(Path.Combine(folder, "Author - Reading.epub")));
        Assert.Contains(host.Store.Events, e => e.Kind == "sent");
    }

    [Fact]
    public void Search_finds_a_book_and_lands_on_it()
    {
        var host = Host("search");
        var folder = Library(("Tolkien - Hobbit.epub", 4));

        using var model = Model(host, Path.Combine(_root, "profile"), folder);

        var hit = Assert.Single(model.Search("hobbit", 5));
        Assert.Equal("Hobbit", hit.Title);

        hit.Open();
        Assert.Equal("Hobbit", model.Selected?.Shown);
    }

    [Fact]
    public void While_off_answers_from_finished_states_and_a_hit_hands_it_to_itself()
    {
        var host = Host("off");
        var folder = Library(("Author - Kept.epub", 5));
        var file = Path.Combine(folder, "Author - Kept.epub");

        using (var model = Model(host, Path.Combine(_root, "profile"), folder))
        {
            model.Selected = model.Rows[0];
            model.Selected.Status = BookStatus.Finished;
        }

        var dormant = new Dormant(host, "meows.bookshelf");
        dormant.Handoffs.Reachable.Add("meows.bookshelf");
        var asleep = new BookshelfPlugin().WhileOff(dormant);
        Assert.NotNull(asleep);

        var hit = Assert.Single(asleep.Search("kept", 6));
        Assert.Equal("Kept", hit.Title);

        hit.Open();
        var (to, what) = Assert.Single(dormant.Handoffs.Sent);
        Assert.Equal("meows.bookshelf", to);
        Assert.Equal(BookshelfPlugin.ShowVerb, what.Verb);
        Assert.Equal(file, what.Note);
    }

    [Fact]
    public void While_off_with_nothing_kept_has_nothing_to_search()
    {
        var dormant = new Dormant(Host("empty-off"), "meows.bookshelf");

        Assert.Null(new BookshelfPlugin().WhileOff(dormant));
    }

    [Fact]
    public void Glance_while_off_says_what_the_tab_would_say()
    {
        var host = Host("glance-off");
        var folder = Library(("Author - Title.epub", 6));

        using (var model = Model(host, Path.Combine(_root, "profile"), folder))
        {
            model.Selected = model.Rows[0];
            model.Selected.Status = BookStatus.Reading;
        }

        var glance = new BookshelfPlugin().GlanceWhileOff(new Dormant(host, "meows.bookshelf"));

        Assert.NotNull(glance);
        Assert.False(glance!.IsTrouble);

        using var open = Model(host, Path.Combine(_root, "profile"), folder);
        Assert.Equal(open.Glance(), glance);
    }

    [Fact]
    public async Task A_rule_stages_the_unfinished_and_declines_with_nowhere_or_nothing()
    {
        var host = Host("rule");
        var folder = Library(("Author - Reading.epub", 7));
        var staging = Path.Combine(_root, "staging");

        using var model = Model(host, Path.Combine(_root, "profile"), folder);
        model.Selected = model.Rows[0];
        model.Selected.Status = BookStatus.Reading;

        ActionRequest Asked(string action) =>
            new(action, new StoredEvent(7, DateTime.Now, "meows.collar", "handled", "x", null,
                new Dictionary<string, string>()));

        // Nowhere to stage to yet.
        await Assert.ThrowsAsync<ActionDeclinedException>(() => model.Perform(Asked(BookshelfPlugin.StageAction), CancellationToken.None));

        host.Picks.Answers.Enqueue(staging);
        model.SetStagingCommand.Execute(null);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (model.StagingText != staging && DateTime.UtcNow < deadline)
            Thread.Sleep(50);

        var said = await model.Perform(Asked(BookshelfPlugin.StageAction), CancellationToken.None);
        Assert.Contains("1 staged", said);
        Assert.True(File.Exists(Path.Combine(staging, "Author - Reading.epub")));

        await Assert.ThrowsAsync<ActionDeclinedException>(() => model.Perform(Asked("juggle"), CancellationToken.None));
    }

    /// <summary>The little a plugin gets while off: its settings and a way to hand itself the thing found.</summary>
    private sealed class Dormant(FakeHost inner, string pluginId) : IMeowsDormantHost
    {
        public string PluginId { get; } = pluginId;

        public string DataDirectory => inner.DataDirectory;

        public IMeowsText Text => MeowsText.Current;

        public IMeowsStore Store => inner.Store;

        public FakeHost.FakeHandoff Handoffs { get; } = new();

        public IMeowsHandoff Handoff => Handoffs;

        public T? LoadSettings<T>() where T : class => inner.LoadSettings<T>();
    }
}
