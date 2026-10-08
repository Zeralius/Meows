using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Vet.Services;
using Meows.Plugins.Vet.ViewModels;
using Meows.Plugins.Vet.Views;

namespace Meows.Plugins.Vet;

public sealed class VetPlugin : IMeowsPlugin
{
    /// <summary>Stable forever. It is the settings key and the activation record.</summary>
    public string Id => "meows.vet";

    public string DisplayName => "Vet";

    public string PlainName => "vet.name.plain";

    public string Description => "vet.description";

    public string Icon => "🩺";

    public string Category => "group.disk";

    public PluginTopics Topics => PluginTopics.Files | PluginTopics.Everyday;

    /// <summary>The job that runs the checkup with no window.</summary>
    public const string CheckJob = "check";

    public IReadOnlyList<RecordedKind> Records =>
    [
        new("checked", "vet.records.checked"),
    ];

    public IReadOnlyList<PluginJob> Jobs =>
    [
        new(CheckJob, "vet.job.check", "vet.job.check.hint"),
    ];

    public Control CreateView(IMeowsHost host) => new VetView
    {
        DataContext = new VetViewModel(host),
    };

    /// <summary>The last checkup is in the settings file, so the Home line needs nothing else.</summary>
    public Glance? GlanceWhileOff(IMeowsDormantHost host) =>
        host.LoadSettings<VetSettings>() is { } settings
            ? Checkup.GlanceOf(settings.LastCheckup, !string.IsNullOrWhiteSpace(settings.BackupFolder), settings.WarnDays, host.Text)
            : null;

    public Task<string> RunJob(string jobId, IMeowsJobHost host, CancellationToken token)
    {
        if (jobId != CheckJob)
            throw new JobDeclinedException(host.Text.Format("vet.job.unknown", jobId));

        var settings = host.LoadSettings<VetSettings>() ?? new VetSettings();
        var backupSet = !string.IsNullOrWhiteSpace(settings.BackupFolder);
        var summary = Checkup.Run(settings.BackupFolder, DateTime.UtcNow);
        summary.BackupStale = backupSet && Checkup.BackupDays(settings.BackupFolder, DateTime.UtcNow) is { } days && days > settings.WarnDays;
        settings.LastCheckup = summary;
        host.SaveSettings(settings);

        var said = Checkup.SummaryOf(summary, backupSet, settings.WarnDays, host.Text);
        if (said.Length == 0)
            return Task.FromResult(host.Text["vet.status.ready"]);

        var judged = Checkup.Judge(summary, backupSet, settings.WarnDays, host.Text);
        host.Notify(host.Text["vet.title"], said, judged.IsTrouble);
        return Task.FromResult(said);
    }
}
