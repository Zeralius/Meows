using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Naptime.Services;
using Meows.Plugins.Naptime.ViewModels;
using Meows.Plugins.Naptime.Views;

namespace Meows.Plugins.Naptime;

public sealed class NaptimePlugin : IMeowsPlugin
{
    /// <summary>Stable forever. It is the settings key and the activation record.</summary>
    public string Id => "meows.naptime";

    public string DisplayName => "Naptime";

    public string PlainName => "naptime.name.plain";

    public string Description => "naptime.description";

    public string Icon => "🌱";

    public string Category => "group.everyday";

    public PluginTopics Topics => PluginTopics.Everyday;

    /// <summary>A handoff Naptime sends itself: show this habit. The note carries the habit's id.</summary>
    public const string ShowVerb = "naptime.show";

    /// <summary>A rule's "tick it", for the habit the rule names.</summary>
    public const string TickAction = "tick";

    public IReadOnlyList<PluginAction> Actions =>
    [
        new(TickAction, "naptime.action.tick", "naptime.action.tick.hint"),
    ];

    public IReadOnlyList<RecordedKind> Records =>
    [
        new("ticked", "naptime.records.ticked"),
    ];

    public Control CreateView(IMeowsHost host) => new NaptimeView
    {
        DataContext = new NaptimeViewModel(host),
    };

    /// <summary>
    /// The habits are in the settings file, so Ctrl+K can reach them while Naptime is off: the
    /// same matching as when it is on, and a hit switches Naptime on and selects the habit by
    /// handing it to itself.
    /// </summary>
    public ISearchable? WhileOff(IMeowsDormantHost host)
    {
        var settings = host.LoadSettings<NaptimeSettings>();
        return settings is null || settings.Habits.Count == 0
            ? null
            : new SleepingNaptime(host, settings.Habits);
    }

    /// <summary>The ticks are in the settings file, so the Home line needs nothing else.</summary>
    public Glance? GlanceWhileOff(IMeowsDormantHost host) =>
        host.LoadSettings<NaptimeSettings>() is { } settings
            ? Streaks.GlanceOf(settings.Habits, DateTime.Today, host.Text)
            : null;

    private sealed class SleepingNaptime(IMeowsDormantHost host, IReadOnlyList<Habit> habits) : ISearchable
    {
        public IReadOnlyList<SearchHit> Search(string query, int limit)
        {
            var words = SearchWords.Split(query);
            var hits = new List<SearchHit>();

            foreach (var habit in habits)
            {
                if (hits.Count >= limit)
                    break;
                var name = habit.Name.Trim().Length > 0 ? habit.Name.Trim() : host.Text["naptime.untitled"];
                if (!SearchWords.Match(words, name))
                    continue;

                var id = habit.Id;
                hits.Add(new SearchHit(name, Streaks.StreakText(Streaks.Of(habit, DateTime.Today), host.Text),
                    () => host.Handoff.Send(host.PluginId, new Handoff(ShowVerb, [], id))));
            }

            return hits;
        }
    }
}
