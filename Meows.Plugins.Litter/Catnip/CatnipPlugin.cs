using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Catnip.ViewModels;
using Meows.Plugins.Catnip.Views;

namespace Meows.Plugins.Catnip;

/// <summary>A page of Litter since 6.1.0, under its own id for its settings; not a plugin of its own.</summary>
internal sealed class CatnipPlugin : IMeowsPlugin
{
    /// <summary>Stable forever. It is the settings key and the activation record.</summary>
    public string Id => "meows.catnip";

    public string DisplayName => "Catnip";

    public string PlainName => "catnip.name.plain";

    public string Description => "catnip.description";

    public string Icon => "🌿";

    public string Category => "group.disk";

    public PluginTopics Topics => PluginTopics.Files;

    public IReadOnlyList<RecordedKind> Records =>
    [
        new("scan", "catnip.records.scan"),
        new("recycled", "catnip.records.recycled"),
    ];

    public Control CreateView(IMeowsHost host) => new CatnipView
    {
        DataContext = new CatnipViewModel(host),
    };
}
