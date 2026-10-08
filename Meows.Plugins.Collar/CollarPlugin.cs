using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Collar.ViewModels;
using Meows.Plugins.Collar.Views;

namespace Meows.Plugins.Collar;

public sealed class CollarPlugin : IMeowsPlugin
{
    public string Id => "meows.collar";

    public string DisplayName => "Collar";

    public string PlainName => "collar.name.plain";

    public string Description => "collar.description";

    public string Icon => "🏷";

    public string Category => "group.everyday";

    /// <summary>A handoff Collar sends itself: show this entry. The note carries the entry's id.</summary>
    public const string ShowVerb = "collar.show";

    /// <summary>A rule's "put it on the list", for today.</summary>
    public const string RemindToday = "remind";

    /// <summary>The same, a week out: for things that want looking at, not today.</summary>
    public const string RemindWeek = "remind-week";

    public IReadOnlyList<PluginAction> Actions =>
    [
        new(RemindToday, "collar.action.today", "collar.action.today.hint"),
        new(RemindWeek, "collar.action.week", "collar.action.week.hint"),
    ];

    public IReadOnlyList<RecordedKind> Records =>
    [
        new("handled", "collar.records.handled"),
        new("snoozed", "collar.records.snoozed"),
    ];

    public Control CreateView(IMeowsHost host) => new CollarView
    {
        DataContext = new CollarViewModel(host),
    };

    /// <summary>
    /// The dates are in the settings file, so Ctrl+K can reach them while Collar is off: the
    /// same matching as when it is on, and a hit switches Collar on and selects the entry by
    /// handing it to itself.
    /// </summary>
    public ISearchable? WhileOff(IMeowsDormantHost host)
    {
        var settings = host.LoadSettings<CollarSettings>();
        return settings is null || settings.Entries.Count == 0 ? null : new SleepingCollar(host, settings.Entries);
    }

    /// <summary>The dates are in the settings file, so the Home line needs nothing else.</summary>
    public Glance? GlanceWhileOff(IMeowsDormantHost host) =>
        host.LoadSettings<CollarSettings>() is { } settings
            ? CollarViewModel.GlanceOf(settings.Entries, settings.LeadDays, DateTime.Today, host.Text)
            : null;

    private sealed class SleepingCollar(IMeowsDormantHost host, IReadOnlyList<Services.CollarEntry> entries) : ISearchable
    {
        public IReadOnlyList<SearchHit> Search(string query, int limit)
        {
            var words = SearchWords.Split(query);
            var hits = new List<SearchHit>();
            foreach (var entry in entries)
            {
                if (hits.Count >= limit)
                    break;
                var kind = host.Text[Services.Dates.Describe(entry.Kind)];
                var file = entry.File is { } path ? System.IO.Path.GetFileName(path) : null;
                if (!SearchWords.Match(words, entry.Title, kind, file))
                    continue;

                var id = entry.Id;
                var shown = entry.Title.Trim().Length > 0 ? entry.Title.Trim() : kind;
                hits.Add(new SearchHit(shown, $"{kind} · {entry.Due:d}",
                    () => host.Handoff.Send(host.PluginId, new Handoff(ShowVerb, [], id))));
            }
            return hits;
        }
    }
}
