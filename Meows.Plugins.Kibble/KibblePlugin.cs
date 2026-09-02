using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Kibble.ViewModels;
using Meows.Plugins.Kibble.Views;

namespace Meows.Plugins.Kibble;

public sealed class KibblePlugin : IMeowsPlugin
{
    public string Id => "meows.kibble";

    public string DisplayName => "Kibble";

    public string Description =>
        "Fills the posting bot's queues. Open a folder, go through it, and send each file to a group.";

    public string? Icon => "🍽";

    public string Category => "Posting bot";

    public Control CreateView(IMeowsHost host) => new KibbleView
    {
        DataContext = new KibbleViewModel(host),
    };
}
