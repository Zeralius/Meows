using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Tin.ViewModels;
using Meows.Plugins.Tin.Views;

namespace Meows.Plugins.Tin;

public sealed class TinPlugin : IMeowsPlugin
{
    public string Id => "meows.tin";

    public string DisplayName => "Tin";

    public string PlainName => "tin.name.plain";

    public string Description => "tin.description";

    public string Icon => "🥫";

    public string Category => "group.everyday";

    public Control CreateView(IMeowsHost host) => new TinView
    {
        DataContext = new TinViewModel(host),
    };
}
