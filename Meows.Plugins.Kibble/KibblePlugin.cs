using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Kibble.ViewModels;
using Meows.Plugins.Kibble.Views;

namespace Meows.Plugins.Kibble;

public sealed class KibblePlugin : IMeowsPlugin
{
    public string Id => "meows.kibble";

    public string DisplayName => "Kibble";

    public string Description => "kibble.description";

    public string? Icon => "🍽";

    public string Category => "group.bot";

    public Control CreateView(IMeowsHost host) => new KibbleView
    {
        DataContext = new KibbleViewModel(host),
    };
}
