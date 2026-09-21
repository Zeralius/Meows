using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Rehome.ViewModels;
using Meows.Plugins.Rehome.Views;

namespace Meows.Plugins.Rehome;

public sealed class RehomePlugin : IMeowsPlugin
{
    /// <summary>Stable forever. It is the settings key and the activation record.</summary>
    public string Id => "meows.rehome";

    public string DisplayName => "Rehome";

    public string PlainName => "rehome.name.plain";

    public string Description => "rehome.description";

    public string Icon => "📦";

    public string Category => "group.disk";

    public Control CreateView(IMeowsHost host) => new RehomeView
    {
        DataContext = new RehomeViewModel(host),
    };
}
