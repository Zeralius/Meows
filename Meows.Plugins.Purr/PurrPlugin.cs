using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Purr.ViewModels;
using Meows.Plugins.Purr.Views;

namespace Meows.Plugins.Purr;

public sealed class PurrPlugin : IMeowsPlugin
{
    /// <summary>Stable forever. It is the settings key and the activation record.</summary>
    public string Id => "meows.purr";

    public string DisplayName => "Purr";

    public string PlainName => "purr.name.plain";

    public string Description => "purr.description";

    public string Icon => "😺";

    public string Category => "group.everyday";

    public Control CreateView(IMeowsHost host) => new PurrView
    {
        DataContext = new PurrViewModel(host),
    };
}
