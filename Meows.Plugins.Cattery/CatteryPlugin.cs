using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Cattery.Services;
using Meows.Plugins.Cattery.ViewModels;
using Meows.Plugins.Cattery.Views;

namespace Meows.Plugins.Cattery;

public sealed class CatteryPlugin : IMeowsPlugin
{
    /// <summary>Stable forever. It is the settings key and the activation record.</summary>
    public string Id => "meows.cattery";

    public string DisplayName => "Cattery";

    public string PlainName => "cattery.name.plain";

    public string Description => "cattery.description";

    public string Icon => "🏠";

    public string Category => "group.everyday";

    public IReadOnlyList<PluginJob> Jobs =>
    [
        new("read", "cattery.job.read", "cattery.job.read.hint"),
    ];

    /// <summary>
    /// Asks git about every repository with no window, and keeps the answer where the tab opens
    /// on it: Meows.exe --do cattery.read, so the list is fresh in the morning.
    /// </summary>
    public async Task<string> RunJob(string jobId, IMeowsJobHost host, CancellationToken token)
    {
        if (jobId != "read")
            throw new JobDeclinedException(host.Text.Format("cattery.job.unknown", jobId));
        var settings = host.LoadSettings<CatterySettings>() ?? new CatterySettings();
        if (settings.Roots.Count == 0)
            throw new JobDeclinedException(host.Text["cattery.status.noroots"]);
        var git = Repos.GitExecutable() ?? throw new JobDeclinedException(host.Text["cattery.error.nogit"]);

        var found = await Task.Run(() => CatteryViewModel.Read(git, settings.Roots, token), token);
        settings.Last = found.ToList();
        settings.LastReadUtc = DateTime.UtcNow;
        host.SaveSettings(settings);
        return host.Text.Format("cattery.summary", found.Count, found.Count(r => r.IsDirty), found.Count(r => r.HasUnpushed));
    }

    public Control CreateView(IMeowsHost host) => new CatteryView
    {
        DataContext = new CatteryViewModel(host),
    };
}
