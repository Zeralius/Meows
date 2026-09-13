using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Scruff.ViewModels;
using Meows.Plugins.Scruff.Views;

namespace Meows.Plugins.Scruff;

public sealed class ScruffPlugin : IMeowsPlugin
{
    public string Id => "meows.scruff";

    public string DisplayName => "Scruff";

    public string Description => "scruff.description";

    public string Icon => "🧹";

    public string Category => "group.everyday";

    public Control CreateView(IMeowsHost host) => new ScruffView
    {
        DataContext = new ScruffViewModel(host),
    };
}
