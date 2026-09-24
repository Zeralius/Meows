using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Trail.ViewModels;
using Meows.Plugins.Trail.Views;

namespace Meows.Plugins.Trail;

public sealed class TrailPlugin : IMeowsPlugin
{
    /// <summary>Stable forever. It is the settings key and the activation record.</summary>
    public string Id => "meows.trail";

    public string DisplayName => "Trail";

    public string PlainName => "trail.name.plain";

    public string Description => "trail.description";

    public string Icon => "🧭";

    public string Category => "group.everyday";

    public IReadOnlyList<RecordedKind> Records =>
    [
        new("tidied", "trail.records.tidied"),
        new("restored", "trail.records.restored"),
    ];

    public Control CreateView(IMeowsHost host) => new TrailView
    {
        DataContext = new TrailViewModel(host),
    };
}
