using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Pantry.Services;
using Meows.Plugins.Pantry.ViewModels;
using Meows.Plugins.Pantry.Views;

namespace Meows.Plugins.Pantry;

public sealed class PantryPlugin : IMeowsPlugin
{
    /// <summary>Stable forever. It is the settings key and the activation record.</summary>
    public string Id => "meows.pantry";

    public string DisplayName => "Pantry";

    public string PlainName => "pantry.name.plain";

    public string Description => "pantry.description";

    public string Icon => "🍲";

    public string Category => "group.everyday";

    public PluginTopics Topics => PluginTopics.Everyday;

    /// <summary>A handoff Pantry sends itself: show this recipe. The note carries the recipe's id.</summary>
    public const string ShowVerb = "pantry.show";

    /// <summary>A rule's "suggest tonight", filling tonight when it is empty.</summary>
    public const string SuggestAction = "suggest";

    /// <summary>The job that reports what runs out with no window.</summary>
    public const string ExpiringJob = "expiring";

    public IReadOnlyList<PluginAction> Actions =>
    [
        new(SuggestAction, "pantry.action.suggest", "pantry.action.suggest.hint"),
    ];

    public IReadOnlyList<RecordedKind> Records =>
    [
        new("added", "pantry.records.added"),
        new("suggested", "pantry.records.suggested"),
        new("cooked", "pantry.records.cooked"),
    ];

    public IReadOnlyList<PluginJob> Jobs =>
    [
        new(ExpiringJob, "pantry.job.expiring", "pantry.job.expiring.hint"),
    ];

    public Control CreateView(IMeowsHost host) => new PantryView
    {
        DataContext = new PantryViewModel(host),
    };

    /// <summary>
    /// The recipes are in the settings file, so Ctrl+K can reach them while Pantry is off: the
    /// same matching as when it is on, and a hit switches Pantry on and selects the recipe by
    /// handing it to itself.
    /// </summary>
    public ISearchable? WhileOff(IMeowsDormantHost host)
    {
        var settings = host.LoadSettings<PantrySettings>();
        return settings is null || settings.Recipes.Count == 0
            ? null
            : new SleepingPantry(host, settings.Recipes);
    }

    /// <summary>The box and the fridge are in the settings file, so the Home line needs nothing else.</summary>
    public Glance? GlanceWhileOff(IMeowsDormantHost host) =>
        host.LoadSettings<PantrySettings>() is { } settings
            ? Cookbook.GlanceOf(settings.Stock, settings.Plan, settings.Recipes, DateTime.Today, host.Text)
            : null;

    public Task<string> RunJob(string jobId, IMeowsJobHost host, CancellationToken token)
    {
        if (jobId != ExpiringJob)
            throw new JobDeclinedException(host.Text.Format("pantry.job.unknown", jobId));

        var settings = host.LoadSettings<PantrySettings>() ?? new PantrySettings();
        Cookbook.RollPlan(settings.Plan, DateTime.Today);
        host.SaveSettings(settings);

        var summary = Cookbook.SummaryOf(settings.Stock, settings.Plan, settings.Recipes, DateTime.Today, host.Text);
        if (summary.Length == 0)
            return Task.FromResult(host.Text["pantry.status.ready"]);

        var trouble = Cookbook.Expired(settings.Stock, DateTime.Today).Count > 0 ||
            Cookbook.Soon(settings.Stock, DateTime.Today).Count > 0;
        host.Notify(host.Text["pantry.title"], summary, trouble);
        return Task.FromResult(summary);
    }

    private sealed class SleepingPantry(IMeowsDormantHost host, IReadOnlyList<Recipe> recipes) : ISearchable
    {
        public IReadOnlyList<SearchHit> Search(string query, int limit)
        {
            var words = SearchWords.Split(query);
            var hits = new List<SearchHit>();

            foreach (var recipe in recipes)
            {
                if (hits.Count >= limit)
                    break;
                var title = recipe.Title.Trim().Length > 0 ? recipe.Title.Trim() : host.Text["pantry.untitled"];
                if (!SearchWords.Match(words, [title, .. recipe.Tags, recipe.Body]))
                    continue;

                var id = recipe.Id;
                hits.Add(new SearchHit(title, Cookbook.TimeText(recipe.Minutes, host.Text),
                    () => host.Handoff.Send(host.PluginId, new Handoff(ShowVerb, [], id))));
            }

            return hits;
        }
    }
}
