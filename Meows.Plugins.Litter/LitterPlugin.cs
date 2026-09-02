using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Litter.ViewModels;
using Meows.Plugins.Litter.Views;

namespace Meows.Plugins.Litter;

public sealed class LitterPlugin : IMeowsPlugin
{
    public string Id => "meows.litter";

    public string DisplayName => "Litter";

    public string Description =>
        "Sorts out the downloads folder: what arrived today, what has been rotting for months, and what never finished downloading at all.";

    public string Icon => "🧺";

    public string Category => "Disk and tidying";

    public Control CreateView(IMeowsHost host) => new LitterView
    {
        DataContext = new LitterViewModel(host),
    };
}
