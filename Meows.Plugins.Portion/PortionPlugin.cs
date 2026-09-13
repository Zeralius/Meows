using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Portion.ViewModels;
using Meows.Plugins.Portion.Views;

namespace Meows.Plugins.Portion;

public sealed class PortionPlugin : IMeowsPlugin
{
    public string Id => "meows.portion";

    public string DisplayName => "Portion";

    public string Description => "portion.description";

    public string? Icon => "⚖️";

    public string Category => "group.bot";

    public Control CreateView(IMeowsHost host) => new PortionView
    {
        DataContext = new PortionViewModel(host),
    };
}
