using Avalonia.Controls;
using Meows.Disk;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Nest.Services;
using Meows.Plugins.Nest.ViewModels;
using Meows.Plugins.Nest.Views;

namespace Meows.Plugins.Nest;

public sealed class NestPlugin : IMeowsPlugin
{
    /// <summary>Stable forever. It is the settings key and the activation record.</summary>
    public string Id => "meows.nest";

    public string DisplayName => "Nest";

    public string PlainName => "nest.name.plain";

    public string Description => "nest.description";

    public string Icon => "🪺";

    public string Category => "group.disk";

    public IReadOnlyList<PluginJob> Jobs =>
    [
        new("copy", "nest.job.copy", "nest.job.copy.hint"),
    ];

    /// <summary>Copy now, with no window: Meows.exe --do nest.copy from Task Scheduler, a stick left in overnight.</summary>
    public async Task<string> RunJob(string jobId, IMeowsJobHost host, CancellationToken token)
    {
        if (jobId != "copy")
            throw new JobDeclinedException(host.Text.Format("nest.job.unknown", jobId));
        var settings = host.LoadSettings<NestSettings>() ?? new NestSettings();
        if (string.IsNullOrWhiteSpace(settings.CopyRoot))
            throw new JobDeclinedException(host.Text["nest.job.nocopyroot"]);
        if (!Directory.Exists(Path.GetPathRoot(settings.CopyRoot)))
            throw new JobDeclinedException(host.Text.Format("nest.job.notthere", settings.CopyRoot));

        var places = NestPlaces.Known().Where(p => Directory.Exists(p.Path) && !settings.Off.Contains(p.Key))
            .Concat(settings.Added.Select(NestPlaces.Added).Where(p => Directory.Exists(p.Path)))
            .ToList();
        var progress = new Progress<string>(file => host.Report(file));
        var outcomes = await Task.Run(() => NestViewModel.CopyAll(places, settings.CopyRoot, progress, token), token);

        var now = DateTime.UtcNow;
        for (var i = 0; i < places.Count; i++)
        {
            if (outcomes[i].Failed.Count == 0)
                settings.LastCopyUtc[places[i].Key] = now;
        }
        host.SaveSettings(settings);

        var copied = outcomes.Sum(o => o.Copied);
        var bytes = outcomes.Sum(o => o.Bytes);
        var failed = outcomes.SelectMany(o => o.Failed).ToList();
        host.Store.Record("copied", settings.CopyRoot, host.Text.Format("nest.journal", copied, FolderSize.Humanise(bytes)),
            new Dictionary<string, string> { ["files"] = copied.ToString(), ["bytes"] = bytes.ToString(), ["failed"] = failed.Count.ToString() });
        if (failed.Count > 0)
            host.Notify(DisplayName, host.Text.Format("nest.copy.failed", failed.Count, string.Join(", ", failed.Take(3))), isTrouble: true);
        return host.Text.Format("nest.copy.done", copied, FolderSize.Humanise(bytes));
    }

    public IReadOnlyList<RecordedKind> Records =>
    [
        new("copied", "nest.records.copied"),
    ];

    public Control CreateView(IMeowsHost host) => new NestView
    {
        DataContext = new NestViewModel(host),
    };
}
