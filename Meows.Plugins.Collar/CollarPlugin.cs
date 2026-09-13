using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Collar.ViewModels;
using Meows.Plugins.Collar.Views;

namespace Meows.Plugins.Collar;

public sealed class CollarPlugin : IMeowsPlugin
{
    public string Id => "meows.collar";

    public string DisplayName => "Collar";

    public string PlainName => "collar.name.plain";

    public string Description => "collar.description";

    public string Icon => "🏷";

    public string Category => "group.everyday";

    public Control CreateView(IMeowsHost host) => new CollarView
    {
        DataContext = new CollarViewModel(host),
    };
}
