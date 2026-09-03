using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Birdwatch.ViewModels;
using Meows.Plugins.Birdwatch.Views;

namespace Meows.Plugins.Birdwatch;

public sealed class BirdwatchPlugin : IMeowsPlugin
{
    public string Id => "meows.birdwatch";

    public string DisplayName => "Birdwatch";

    public string Description => "birdwatch.description";

    public string Icon => "🐦";

    public string Category => "group.everyday";

    public Control CreateView(IMeowsHost host) => new BirdwatchView
    {
        DataContext = new BirdwatchViewModel(host),
    };
}
