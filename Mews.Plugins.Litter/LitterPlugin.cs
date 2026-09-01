using Avalonia.Controls;
using Mews.Plugins.Abstractions;
using Mews.Plugins.Litter.ViewModels;
using Mews.Plugins.Litter.Views;

namespace Mews.Plugins.Litter;

public sealed class LitterPlugin : IMewsPlugin
{
    public string Id => "mews.litter";

    public string DisplayName => "Litter";

    public string Description =>
        "Sorts out the downloads folder: what arrived today, what has been rotting for months, and what never finished downloading at all.";

    public string Icon => "🧺";

    public Control CreateView(IMewsHost host) => new LitterView
    {
        DataContext = new LitterViewModel(host),
    };
}
