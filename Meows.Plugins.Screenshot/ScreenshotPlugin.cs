using Avalonia.Controls;
using Meows.Disk;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Screenshot.Services;
using Meows.Plugins.Screenshot.ViewModels;
using Meows.Plugins.Screenshot.Views;

namespace Meows.Plugins.Screenshot;

public sealed class ScreenshotPlugin : IMeowsPlugin
{
    /// <summary>Stable forever. It is the settings key and the activation record.</summary>
    public string Id => "meows.screenshot";

    public string DisplayName => "Screenshot";

    public string PlainName => "screenshot.name.plain";

    public string Description => "screenshot.description";

    public string Icon => "📸";

    public string Category => "group.disk";

    public PluginTopics Topics => PluginTopics.Gaming | PluginTopics.Social;

    /// <summary>A handoff Screenshot sends itself: show this shot. The note carries the shot's path.</summary>
    public const string ShowVerb = "screenshot.show";

    /// <summary>A rule's "recycle the identical copies".</summary>
    public const string RecycleAction = "recycle-duplicates";

    /// <summary>The job that scans with no window.</summary>
    public const string ScanJob = "scan";

    public IReadOnlyList<PluginAction> Actions =>
    [
        new(RecycleAction, "screenshot.action.recycle", "screenshot.action.recycle.hint"),
    ];

    public IReadOnlyList<RecordedKind> Records =>
    [
        new("scanned", "screenshot.records.scanned"),
        new("recycled", "screenshot.records.recycled"),
        new("kept", "screenshot.records.kept"),
        new("sent", "screenshot.records.sent"),
    ];

    public IReadOnlyList<PluginJob> Jobs =>
    [
        new(ScanJob, "screenshot.job.scan", "screenshot.job.scan.hint"),
    ];

    public Control CreateView(IMeowsHost host) => new ScreenshotView
    {
        DataContext = new ScreenshotViewModel(host),
    };

    /// <summary>
    /// Best-of picks are in the settings file, so Ctrl+K can reach them while Screenshot is off:
    /// a hit switches Screenshot on and selects the shot by handing it to itself.
    /// </summary>
    public ISearchable? WhileOff(IMeowsDormantHost host)
    {
        var settings = host.LoadSettings<ScreenshotSettings>();
        var kept = settings?.KeptPaths.Where(File.Exists).ToList() ?? [];
        return kept.Count == 0 ? null : new SleepingScreenshots(host, kept);
    }

    /// <summary>The last scan is in the settings file, so the Home line needs nothing else.</summary>
    public Glance? GlanceWhileOff(IMeowsDormantHost host) =>
        host.LoadSettings<ScreenshotSettings>() is { } settings
            ? Shots.GlanceOf(settings.LastSummary, host.Text)
            : null;

    public Task<string> RunJob(string jobId, IMeowsJobHost host, CancellationToken token)
    {
        if (jobId != ScanJob)
            throw new JobDeclinedException(host.Text.Format("screenshot.job.unknown", jobId));

        var settings = host.LoadSettings<ScreenshotSettings>() ?? new ScreenshotSettings();
        var shots = ScreenshotViewModel.RunScan(settings.Folders);
        settings.LastSummary = Shots.Summarise(shots, DateTime.UtcNow);
        host.SaveSettings(settings);

        var summary = settings.LastSummary;
        var said = Shots.SummaryOf(summary, host.Text);
        if (said.Length == 0)
            return Task.FromResult(host.Text["screenshot.status.ready"]);

        host.Notify(host.Text["screenshot.title"], said, summary.Duplicates > 0);
        return Task.FromResult(said);
    }

    private sealed class SleepingScreenshots(IMeowsDormantHost host, IReadOnlyList<string> kept) : ISearchable
    {
        public IReadOnlyList<SearchHit> Search(string query, int limit)
        {
            var words = SearchWords.Split(query);
            var hits = new List<SearchHit>();

            foreach (var path in kept)
            {
                if (hits.Count >= limit)
                    break;
                var name = Path.GetFileName(path);
                if (!SearchWords.Match(words, name))
                    continue;

                hits.Add(new SearchHit(name, host.Text["screenshot.kept"],
                    () => host.Handoff.Send(host.PluginId, new Handoff(ShowVerb, [], path))));
            }

            return hits;
        }
    }
}
