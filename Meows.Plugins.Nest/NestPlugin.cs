using Avalonia.Controls;
using Meows.Plugins.Abstractions;
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

    public IReadOnlyList<RecordedKind> Records =>
    [
        new("copied", "nest.records.copied"),
    ];

    public Control CreateView(IMeowsHost host) => new NestView
    {
        DataContext = new NestViewModel(host),
    };
}
