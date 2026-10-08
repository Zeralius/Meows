using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Basket.Services;
using Meows.Plugins.Basket.ViewModels;
using Meows.Plugins.Basket.Views;

namespace Meows.Plugins.Basket;

public sealed class BasketPlugin : IMeowsPlugin
{
    /// <summary>Stable forever. It is the settings key and the activation record.</summary>
    public string Id => "meows.basket";

    public string DisplayName => "Basket";

    public string PlainName => "basket.name.plain";

    public string Description => "basket.description";

    public string Icon => "🧺";

    public string Category => "group.everyday";

    /// <summary>A handoff Basket sends itself: show this card. The note carries the card's id.</summary>
    public const string ShowVerb = "basket.show";

    /// <summary>A rule's "add a card", in the first list.</summary>
    public const string AddAction = "add";

    /// <summary>The job that reports due cards with no window.</summary>
    public const string DueJob = "due";

    public IReadOnlyList<PluginAction> Actions =>
    [
        new(AddAction, "basket.action.add", "basket.action.add.hint"),
    ];

    public IReadOnlyList<RecordedKind> Records =>
    [
        new("added", "basket.records.added"),
        new("moved", "basket.records.moved"),
        new("finished", "basket.records.finished"),
    ];

    public IReadOnlyList<PluginJob> Jobs =>
    [
        new(DueJob, "basket.job.due", "basket.job.due.hint"),
    ];

    public Control CreateView(IMeowsHost host) => new BasketView
    {
        DataContext = new BasketViewModel(host),
    };

    /// <summary>
    /// The cards are in the settings file, so Ctrl+K can reach them while Basket is off: the
    /// same matching as when it is on, and a hit switches Basket on and selects the card by
    /// handing it to itself.
    /// </summary>
    public ISearchable? WhileOff(IMeowsDormantHost host)
    {
        var settings = host.LoadSettings<BasketSettings>();
        return settings is null || Board.AllCards(settings.Lists).Count() == 0
            ? null
            : new SleepingBasket(host, settings.Lists);
    }

    /// <summary>The board is in the settings file, so the Home line needs nothing else.</summary>
    public Glance? GlanceWhileOff(IMeowsDormantHost host) =>
        host.LoadSettings<BasketSettings>() is { } settings
            ? Board.GlanceOf(settings.Lists, DateTime.Today, host.Text)
            : null;

    public Task<string> RunJob(string jobId, IMeowsJobHost host, CancellationToken token)
    {
        if (jobId != DueJob)
            throw new JobDeclinedException(host.Text.Format("basket.job.unknown", jobId));

        var settings = host.LoadSettings<BasketSettings>() ?? new BasketSettings();
        var summary = Board.SummaryOf(settings.Lists, DateTime.Today, host.Text);
        if (summary.Length == 0)
            return Task.FromResult(host.Text["basket.status.ready"]);

        host.Notify(host.Text["basket.title"], summary, Board.Overdue(settings.Lists, DateTime.Today).Count > 0);
        return Task.FromResult(summary);
    }

    private sealed class SleepingBasket(IMeowsDormantHost host, IReadOnlyList<BasketList> lists) : ISearchable
    {
        public IReadOnlyList<SearchHit> Search(string query, int limit)
        {
            var words = SearchWords.Split(query);
            var hits = new List<SearchHit>();

            foreach (var (list, card) in Board.AllCards(lists))
            {
                if (hits.Count >= limit)
                    break;

                var title = card.Title.Trim().Length > 0 ? card.Title.Trim() : host.Text["basket.untitled"];
                var file = card.LinkPath is { } path ? Path.GetFileName(path) : null;
                if (!SearchWords.Match(words, title, card.Notes, list.Title, file))
                    continue;

                var id = card.Id;
                hits.Add(new SearchHit(title, list.Title,
                    () => host.Handoff.Send(host.PluginId, new Handoff(ShowVerb, [], id))));
            }

            return hits;
        }
    }
}
