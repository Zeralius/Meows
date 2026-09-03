using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Purrge.ViewModels;
using Meows.Plugins.Purrge.Views;

namespace Meows.Plugins.Purrge;

public sealed class PurrgePlugin : IMeowsPlugin
{
    public string Id => "meows.purrge";

    public string DisplayName => "Purrge";

    public string Description => "purrge.description";

    public string Icon => "🐾";

    public string Category => "group.disk";

    public Control CreateView(IMeowsHost host) => new PurrgeView
    {
        DataContext = new PurrgeViewModel(host),
    };
}
