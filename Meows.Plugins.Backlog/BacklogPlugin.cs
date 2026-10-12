using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Backlog.Services;
using Meows.Plugins.Backlog.ViewModels;
using Meows.Plugins.Backlog.Views;
using Meows.Plugins.Larder;

namespace Meows.Plugins.Backlog;

/// <summary>
/// Games, in two pages: what to play next, with ratings and a picker, and every installed Steam
/// game by size and when it was last played (Larder until 6.2.0). Both read the same Steam
/// libraries and were two tabs about one shelf. Backlog keeps its id, so its pile, ratings and
/// history carry straight on; Larder reads its own settings from a folder under Backlog's.
/// </summary>
public sealed class BacklogPlugin() : PagedPlugin(new PilePage(), new LarderPlugin())
{
    /// <summary>A handoff Backlog sends itself: show this game. The note carries the entry's id.</summary>
    public const string ShowVerb = "backlog.show";

    /// <summary>A rule's "pick a game for tonight".</summary>
    public const string PickAction = "pick";

    /// <summary>The job that picks a game with no window.</summary>
    public const string PickJob = "pick";

    public override string PlainName => "backlog.name.plain";

    public override string Description => "backlog.description";

    public override string? Category => "group.everyday";
}

/// <summary>The first page: the pile, as Backlog has always kept it.</summary>
internal sealed class PilePage : IMeowsPlugin
{
    /// <summary>Stable forever. It is the settings key and the activation record.</summary>
    public string Id => "meows.backlog";

    public string DisplayName => "Backlog";

    public string PlainName => "backlog.page.plain";

    public string Description => "backlog.page.description";

    public string Icon => "🎮";

    public string Category => "group.everyday";

    public PluginTopics Topics => PluginTopics.Gaming;

    public IReadOnlyList<PluginAction> Actions =>
    [
        new(BacklogPlugin.PickAction, "backlog.action.pick", "backlog.action.pick.hint"),
    ];

    public IReadOnlyList<RecordedKind> Records =>
    [
        new("picked", "backlog.records.picked"),
        new("started", "backlog.records.started"),
        new("finished", "backlog.records.finished"),
    ];

    public IReadOnlyList<PluginJob> Jobs =>
    [
        new(BacklogPlugin.PickJob, "backlog.job.pick", "backlog.job.pick.hint"),
    ];

    public Control CreateView(IMeowsHost host) => new BacklogView
    {
        DataContext = new BacklogViewModel(host),
    };

    /// <summary>
    /// The pile is in the settings file, so Ctrl+K can reach it while Backlog is off: the same
    /// matching as when it is on, and a hit switches Backlog on and selects the game by handing
    /// it to itself.
    /// </summary>
    public ISearchable? WhileOff(IMeowsDormantHost host)
    {
        var settings = host.LoadSettings<BacklogSettings>();
        return settings is null || settings.Entries.Count == 0
            ? null
            : new SleepingBacklog(host, settings.Entries);
    }

    /// <summary>The pile is in the settings file, so the Home line needs nothing else.</summary>
    public Glance? GlanceWhileOff(IMeowsDormantHost host) =>
        host.LoadSettings<BacklogSettings>() is { } settings
            ? Pile.GlanceOf(settings.Entries, host.Text)
            : null;

    public Task<string> RunJob(string jobId, IMeowsJobHost host, CancellationToken token)
    {
        if (jobId != BacklogPlugin.PickJob)
            throw new JobDeclinedException(host.Text.Format("backlog.job.unknown", jobId));

        var settings = host.LoadSettings<BacklogSettings>() ?? new BacklogSettings();
        var picked = Pile.Pick(settings.Entries, new Random());
        if (picked is null)
            throw new JobDeclinedException(host.Text["backlog.status.nothing"]);

        var said = host.Text.Format("backlog.status.picked", picked.Name);
        host.Notify(host.Text["backlog.title"], said);
        return Task.FromResult(said);
    }

    private sealed class SleepingBacklog(IMeowsDormantHost host, IReadOnlyList<BacklogEntry> entries) : ISearchable
    {
        public IReadOnlyList<SearchHit> Search(string query, int limit)
        {
            var words = SearchWords.Split(query);
            var hits = new List<SearchHit>();

            foreach (var entry in entries)
            {
                if (hits.Count >= limit)
                    break;
                var title = entry.Name.Trim().Length > 0 ? entry.Name.Trim() : host.Text["backlog.untitled"];
                if (!SearchWords.Match(words, title))
                    continue;

                var id = entry.Id;
                hits.Add(new SearchHit(title, host.Text[Pile.Describe(entry.Status)],
                    () => host.Handoff.Send(host.PluginId, new Handoff(BacklogPlugin.ShowVerb, [], id))));
            }

            return hits;
        }
    }
}
