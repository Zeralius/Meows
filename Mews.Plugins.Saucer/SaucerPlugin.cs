using Avalonia.Controls;
using Mews.Plugins.Abstractions;
using Mews.Plugins.Saucer.ViewModels;
using Mews.Plugins.Saucer.Views;

namespace Mews.Plugins.Saucer;

public sealed class SaucerPlugin : IMewsPlugin
{
    public string Id => "mews.saucer";

    public string DisplayName => "Saucer";

    public string Description =>
        "Keeps what you copy, images included, and drops them into an intake folder Kibble can sort.";

    public string Icon => "🥛";

    public string Category => "Everyday";

    public Control CreateView(IMewsHost host) => new SaucerView
    {
        DataContext = new SaucerViewModel(host),
    };
}
