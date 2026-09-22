using Avalonia.Controls;
using Meows.Plugins.Abstractions;

namespace Yowl;

public sealed class YowlPlugin : IMeowsPlugin
{
    public string Id => "example.yowl";

    public string DisplayName => "Yowl";

    /// <summary>A key from the Strings folder, so the card reads in whatever language the window is in.</summary>
    public string PlainName => "yowl.name.plain";

    public string Description => "yowl.description";

    public string? Icon => "📣";

    public string? Category => "Examples";

    public Control CreateView(IMeowsHost host) => new YowlView
    {
        DataContext = new YowlViewModel(host),
    };
}
