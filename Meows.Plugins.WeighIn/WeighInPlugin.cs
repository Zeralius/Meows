using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.WeighIn.ViewModels;
using Meows.Plugins.WeighIn.Views;

namespace Meows.Plugins.WeighIn;

public sealed class WeighInPlugin : IMeowsPlugin
{
    /// <summary>Stable forever. It is the settings key and the activation record.</summary>
    public string Id => "meows.weighin";

    public string DisplayName => "Weigh-In";

    public string PlainName => "weighin.name.plain";

    public string Description => "weighin.description";

    public string Icon => "⚖️";

    public string Category => "group.disk";

    /// <summary>A rule's "take a reading now".</summary>
    public const string MeasureAction = "measure";

    public IReadOnlyList<PluginAction> Actions =>
    [
        new(MeasureAction, "weighin.action.measure", "weighin.action.measure.hint"),
    ];

    /// <summary>The reading as a job: Meows.exe --do weighin.measure, from Task Scheduler.</summary>
    public const string MeasureJob = "measure";

    public IReadOnlyList<PluginJob> Jobs =>
    [
        new(MeasureJob, "weighin.job.measure", "weighin.job.measure.hint") { StandsInForSchedule = true },
    ];

    /// <summary>
    /// The same reading the tab takes on its schedule, saved, pruned and journaled the same way,
    /// with no window. A folder over its budget is said as a notification, since nobody is
    /// looking at the tab. While Windows runs this, the tab's own schedule stands down.
    /// </summary>
    public async Task<string> RunJob(string jobId, IMeowsJobHost host, CancellationToken token)
    {
        if (jobId != MeasureJob)
            throw new JobDeclinedException(host.Text.Format("weighin.job.unknown", jobId));

        var settings = host.LoadSettings<WeighInSettings>() ?? new WeighInSettings();
        var folder = System.IO.Path.Combine(host.DataDirectory, "readings");
        var roots = WeighInViewModel.RootsFor(settings);
        if (roots.Count == 0)
            throw new JobDeclinedException(host.Text["weighin.job.nodrives"]);

        var before = Services.Readings.Load(folder);
        var reading = await Task.Run(() => Services.Readings.Take(roots, settings.Depth, settings.SkipSystemFolders,
            root => host.Report(host.Text.Format("weighin.progress", root)), token, settings.Budgets.Select(b => b.Path).ToList()), token);

        Services.Readings.Save(folder, reading);
        Services.Readings.Prune(folder, Math.Max(2, settings.KeepReadings));
        var previous = before.Count > 0 ? before[^1] : null;
        WeighInViewModel.JournalReading(host.Store, host.Text, reading, previous);
        WeighInViewModel.JournalCrossings(host.Store, host.Text, settings.Budgets, reading, previous);

        if (WeighInViewModel.BudgetGlance(settings.Budgets, reading, host.Text) is { IsTrouble: true } over)
            host.Notify(DisplayName, over.Text, isTrouble: true);

        return host.Text.Format("weighin.status.read", reading.Drives.Count, reading.At.ToString("HH:mm"));
    }

    public IReadOnlyList<RecordedKind> Records =>
    [
        new("reading", "weighin.records.reading"),
        new("over-budget", "weighin.records.overbudget"),
    ];

    /// <summary>
    /// With no tab open there is no drive selected to tell the story of, so the line is when the
    /// last reading was, the same one the tab falls back to. The readings are small files; the
    /// drives themselves are never touched for this.
    /// </summary>
    public Glance? GlanceWhileOff(IMeowsDormantHost host)
    {
        var readings = Services.Readings.Load(System.IO.Path.Combine(host.DataDirectory, "readings"));
        var budgets = host.LoadSettings<WeighInSettings>()?.Budgets ?? [];
        return WeighInViewModel.BudgetGlance(budgets, readings.Count > 0 ? readings[^1] : null, host.Text)
               ?? new(WeighInViewModel.SummaryOf(readings, host.Text));
    }

    public Control CreateView(IMeowsHost host) => new WeighInView
    {
        DataContext = new WeighInViewModel(host),
    };
}
