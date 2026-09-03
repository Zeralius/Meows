using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Mouser.ViewModels;
using Meows.Plugins.Mouser.Views;

namespace Meows.Plugins.Mouser;

public sealed class MouserPlugin : IMeowsPlugin
{
    public string Id => "meows.mouser";

    public string DisplayName => "Mouser";

    public string Description => "mouser.description";

    public string Icon => "🐁";

    public string Category => "group.disk";

    public Control CreateView(IMeowsHost host) => new MouserView
    {
        DataContext = new MouserViewModel(host),
    };
}
