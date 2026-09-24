using Avalonia.Controls;
using Meows.Plugins.Abstractions;
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

    public Control CreateView(IMeowsHost host) => new CatteryView
    {
        DataContext = new CatteryViewModel(host),
    };
}
