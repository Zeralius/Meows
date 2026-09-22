using Avalonia.Controls;
using Meows.Plugins.Abstractions;

namespace Sniff;

public sealed class SniffPlugin : IMeowsPlugin
{
    public string Id => "example.sniff";

    public string DisplayName => "Sniff";

    public string PlainName => "sniff.name.plain";

    public string Description => "sniff.description";

    public string? Icon => "👃";

    public string? Category => "Examples";

    public Control CreateView(IMeowsHost host) => new SniffView
    {
        DataContext = new SniffViewModel(host),
    };
}
