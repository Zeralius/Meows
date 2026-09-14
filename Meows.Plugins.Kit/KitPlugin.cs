using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Kit.ViewModels;
using Meows.Plugins.Kit.Views;

namespace Meows.Plugins.Kit;

public sealed class KitPlugin : IMeowsPlugin
{
    /// <summary>Stable forever. It is the settings key and the activation record.</summary>
    public string Id => "meows.kit";

    public string DisplayName => "Kit";

    public string PlainName => "kit.name.plain";

    public string Description => "kit.description";

    public string Icon => "🧺";

    public string Category => "group.everyday";

    public Control CreateView(IMeowsHost host) => new KitView
    {
        DataContext = new KitViewModel(host),
    };
}
