using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Larder.ViewModels;
using Meows.Plugins.Larder.Views;

namespace Meows.Plugins.Larder;

public sealed class LarderPlugin : IMeowsPlugin
{
    /// <summary>Stable forever. It is the settings key and the activation record.</summary>
    public string Id => "meows.larder";

    public string DisplayName => "Larder";

    public string PlainName => "larder.name.plain";

    public string Description => "larder.description";

    public string Icon => "🧀";

    public string Category => "group.disk";

    public Control CreateView(IMeowsHost host) => new LarderView
    {
        DataContext = new LarderViewModel(host),
    };
}
