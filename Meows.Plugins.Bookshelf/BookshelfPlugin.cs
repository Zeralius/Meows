using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Bookshelf.Services;
using Meows.Plugins.Bookshelf.ViewModels;
using Meows.Plugins.Bookshelf.Views;

namespace Meows.Plugins.Bookshelf;

public sealed class BookshelfPlugin : IMeowsPlugin
{
    /// <summary>Stable forever. It is the settings key and the activation record.</summary>
    public string Id => "meows.bookshelf";

    public string DisplayName => "Bookshelf";

    public string PlainName => "bookshelf.name.plain";

    public string Description => "bookshelf.description";

    public string Icon => "📚";

    public string Category => "group.everyday";

    public PluginTopics Topics => PluginTopics.Everyday;

    /// <summary>A handoff Bookshelf sends itself: show this book. The note carries the book's path.</summary>
    public const string ShowVerb = "bookshelf.show";

    /// <summary>A rule's "stage the unfinished".</summary>
    public const string StageAction = "stage-unfinished";

    public IReadOnlyList<PluginAction> Actions =>
    [
        new(StageAction, "bookshelf.action.stage", "bookshelf.action.stage.hint"),
    ];

    public IReadOnlyList<RecordedKind> Records =>
    [
        new("scanned", "bookshelf.records.scanned"),
        new("finished", "bookshelf.records.finished"),
        new("sent", "bookshelf.records.sent"),
        new("recycled", "bookshelf.records.recycled"),
    ];

    public Control CreateView(IMeowsHost host) => new BookshelfView
    {
        DataContext = new BookshelfViewModel(host),
    };

    /// <summary>
    /// Finished states are in the settings file, so Ctrl+K can reach them while Bookshelf is
    /// off: a hit switches Bookshelf on and selects the book by handing it to itself.
    /// </summary>
    public ISearchable? WhileOff(IMeowsDormantHost host)
    {
        var settings = host.LoadSettings<BookshelfSettings>();
        var kept = settings?.States.Where(s => File.Exists(s.Path)).ToList() ?? [];
        return kept.Count == 0 ? null : new SleepingBookshelf(host, kept);
    }

    /// <summary>The last scan is in the settings file, so the Home line needs nothing else.</summary>
    public Glance? GlanceWhileOff(IMeowsDormantHost host) =>
        host.LoadSettings<BookshelfSettings>() is { } settings
            ? Library.GlanceOf(settings.LastSummary, host.Text)
            : null;

    private sealed class SleepingBookshelf(IMeowsDormantHost host, IReadOnlyList<BookState> states) : ISearchable
    {
        public IReadOnlyList<SearchHit> Search(string query, int limit)
        {
            var words = SearchWords.Split(query);
            var hits = new List<SearchHit>();

            foreach (var state in states)
            {
                if (hits.Count >= limit)
                    break;
                var (author, title) = Library.ParseName(state.Path);
                var shown = title.Length > 0 ? title : Path.GetFileName(state.Path);
                if (!SearchWords.Match(words, shown, author))
                    continue;

                var path = state.Path;
                hits.Add(new SearchHit(shown, host.Text[Library.Describe(state.Status)],
                    () => host.Handoff.Send(host.PluginId, new Handoff(ShowVerb, [], path))));
            }

            return hits;
        }
    }
}
