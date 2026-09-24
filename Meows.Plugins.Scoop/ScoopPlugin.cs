using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Scoop.ViewModels;
using Meows.Plugins.Scoop.Views;

namespace Meows.Plugins.Scoop;

public sealed class ScoopPlugin : IMeowsPlugin
{
    /// <summary>Stable forever. It is the settings key and the activation record.</summary>
    public string Id => "meows.scoop";

    public string DisplayName => "Scoop";

    public string PlainName => "scoop.name.plain";

    public string Description => "scoop.description";

    public string Icon => "🗑";

    public string Category => "group.disk";

    public IReadOnlyList<RecordedKind> Records =>
    [
        new("restored", "scoop.records.restored"),
        new("emptied", "scoop.records.emptied"),
    ];

    public Control CreateView(IMeowsHost host) => new ScoopView
    {
        DataContext = new ScoopViewModel(host),
    };
}
