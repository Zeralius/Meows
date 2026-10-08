using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Purrge.ViewModels;
using Meows.Plugins.Purrge.Views;

namespace Meows.Plugins.Purrge;

public sealed class PurrgePlugin : IMeowsPlugin
{
    public string Id => "meows.purrge";

    public string DisplayName => "Purrge";

    public string PlainName => "purrge.name.plain";

    public string Description => "purrge.description";

    public string Icon => "🐾";

    public string Category => "group.disk";

    /// <summary>A rule's "look for duplicates in its folder".</summary>
    public const string ScanAction = "scan";

    public IReadOnlyList<PluginAction> Actions =>
    [
        new(ScanAction, "purrge.action.scan", "purrge.action.scan.hint"),
    ];

    public IReadOnlyList<RecordedKind> Records =>
    [
        new("recycled", "purrge.records.recycled"),
        new("renamed", "purrge.records.renamed"),
    ];

    public Control CreateView(IMeowsHost host) => new PurrgeView
    {
        DataContext = new PurrgeViewModel(host),
    };
}
