using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Portion.ViewModels;
using Meows.Plugins.Portion.Views;

namespace Meows.Plugins.Portion;

public sealed class PortionPlugin : IMeowsPlugin
{
    public string Id => "meows.portion";

    public string DisplayName => "Portion";

    public string PlainName => "portion.name.plain";

    public string Description => "portion.description";

    public string? Icon => "⚖️";

    public string Category => "group.bot";

    /// <summary>A rule's "weigh the queues now".</summary>
    public const string CheckAction = "check";

    public IReadOnlyList<PluginAction> Actions =>
    [
        new(CheckAction, "portion.action.check", "portion.action.check.hint"),
    ];

    public IReadOnlyList<RecordedKind> Records =>
    [
        new("shrunk", "portion.records.shrunk"),
        new("held", "portion.records.held"),
    ];

    public Control CreateView(IMeowsHost host) => new PortionView
    {
        DataContext = new PortionViewModel(host),
    };
}
