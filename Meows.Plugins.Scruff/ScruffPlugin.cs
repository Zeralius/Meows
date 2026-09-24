using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Scruff.ViewModels;
using Meows.Plugins.Scruff.Views;

namespace Meows.Plugins.Scruff;

public sealed class ScruffPlugin : IMeowsPlugin
{
    public string Id => "meows.scruff";

    public string DisplayName => "Scruff";

    public string PlainName => "scruff.name.plain";

    public string Description => "scruff.description";

    public string Icon => "🧹";

    public string Category => "group.everyday";

    /// <summary>A rule's "take the metadata out of it, where it is".</summary>
    public const string CleanAction = "clean";

    public IReadOnlyList<PluginAction> Actions =>
    [
        new(CleanAction, "scruff.action.clean", "scruff.action.clean.hint"),
    ];

    public IReadOnlyList<RecordedKind> Records =>
    [
        new("posted", "scruff.records.posted"),
    ];

    public Control CreateView(IMeowsHost host) => new ScruffView
    {
        DataContext = new ScruffViewModel(host),
    };
}
