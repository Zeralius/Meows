using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Litter.ViewModels;
using Meows.Plugins.Litter.Views;

namespace Meows.Plugins.Litter;

public sealed class LitterPlugin : IMeowsPlugin
{
    public string Id => "meows.litter";

    public string DisplayName => "Litter";

    public string Description => "litter.description";

    public string Icon => "🧺";

    public string Category => "group.disk";

    public Control CreateView(IMeowsHost host) => new LitterView
    {
        DataContext = new LitterViewModel(host),
    };
}
