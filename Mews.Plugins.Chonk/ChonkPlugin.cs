using Avalonia.Controls;
using Mews.Plugins.Abstractions;
using Mews.Plugins.Chonk.ViewModels;
using Mews.Plugins.Chonk.Views;

namespace Mews.Plugins.Chonk;

public sealed class ChonkPlugin : IMewsPlugin
{
    public string Id => "mews.chonk";

    public string DisplayName => "Chonk";

    public string Description =>
        "Measures where the room on a drive went, biggest first, and clears out what you no longer want.";

    public string Icon => "🐈";

    public string Category => "Disk and tidying";

    public Control CreateView(IMewsHost host) => new ChonkView
    {
        DataContext = new ChonkViewModel(host),
    };
}
