using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Saucer.ViewModels;
using Meows.Plugins.Saucer.Views;

namespace Meows.Plugins.Saucer;

public sealed class SaucerPlugin : IMeowsPlugin
{
    public string Id => "meows.saucer";

    public string DisplayName => "Saucer";

    public string Description =>
        "Keeps what you copy, images included, and drops them into an intake folder Kibble can sort.";

    public string Icon => "🥛";

    public string Category => "Everyday";

    public Control CreateView(IMeowsHost host) => new SaucerView
    {
        DataContext = new SaucerViewModel(host),
    };
}
