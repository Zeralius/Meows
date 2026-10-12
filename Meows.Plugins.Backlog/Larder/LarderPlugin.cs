using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Larder.ViewModels;
using Meows.Plugins.Larder.Views;

namespace Meows.Plugins.Larder;

/// <summary>A page of Backlog since 6.2.0, under its own id for its settings; not a plugin of its own.</summary>
internal sealed class LarderPlugin : IMeowsPlugin
{
    /// <summary>Stable forever. It is the settings key and the activation record.</summary>
    public string Id => "meows.larder";

    public string DisplayName => "Larder";

    public string PlainName => "larder.name.plain";

    public string Description => "larder.description";

    public string Icon => "🧀";

    public string Category => "group.disk";

    public PluginTopics Topics => PluginTopics.Gaming;

    public Control CreateView(IMeowsHost host) => new LarderView
    {
        DataContext = new LarderViewModel(host),
    };
}
