using Avalonia.Controls;
using Mews.Plugins.Abstractions;
using Mews.Plugins.Kibble.ViewModels;
using Mews.Plugins.Kibble.Views;

namespace Mews.Plugins.Kibble;

public sealed class KibblePlugin : IMewsPlugin
{
    public string Id => "mews.kibble";

    public string DisplayName => "Kibble";

    public string Description =>
        "Fills the posting bot's queues. Open a folder, go through it, and send each file to a group.";

    public string? Icon => "🍽";

    public string Category => "Posting bot";

    public Control CreateView(IMewsHost host) => new KibbleView
    {
        DataContext = new KibbleViewModel(host),
    };
}
