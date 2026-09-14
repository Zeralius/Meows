using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Catnip.ViewModels;
using Meows.Plugins.Catnip.Views;

namespace Meows.Plugins.Catnip;

public sealed class CatnipPlugin : IMeowsPlugin
{
    /// <summary>Stable forever. It is the settings key and the activation record.</summary>
    public string Id => "meows.catnip";

    public string DisplayName => "Catnip";

    public string PlainName => "catnip.name.plain";

    public string Description => "catnip.description";

    public string Icon => "🌿";

    public string Category => "group.disk";

    public Control CreateView(IMeowsHost host) => new CatnipView
    {
        DataContext = new CatnipViewModel(host),
    };
}
