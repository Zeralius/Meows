using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Chonk.ViewModels;
using Meows.Plugins.Chonk.Views;

namespace Meows.Plugins.Chonk;

public sealed class ChonkPlugin : IMeowsPlugin
{
    public string Id => "meows.chonk";

    public string DisplayName => "Chonk";

    public string Description => "chonk.description";

    public string Icon => "🐈";

    public string Category => "group.disk";

    public Control CreateView(IMeowsHost host) => new ChonkView
    {
        DataContext = new ChonkViewModel(host),
    };
}
