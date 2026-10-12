using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Mouser.ViewModels;
using Meows.Plugins.Mouser.Views;

namespace Meows.Plugins.Mouser;

/// <summary>A page of Litter since 6.1.0, under its own id for its settings; not a plugin of its own.</summary>
internal sealed class MouserPlugin : IMeowsPlugin
{
    public string Id => "meows.mouser";

    public string DisplayName => "Mouser";

    public string PlainName => "mouser.name.plain";

    public string Description => "mouser.description";

    public string Icon => "🐁";

    public string Category => "group.disk";

    public PluginTopics Topics => PluginTopics.Files;

    public Control CreateView(IMeowsHost host) => new MouserView
    {
        DataContext = new MouserViewModel(host),
    };
}
