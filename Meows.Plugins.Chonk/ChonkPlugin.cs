using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Chonk.ViewModels;
using Meows.Plugins.Chonk.Views;

namespace Meows.Plugins.Chonk;

public sealed class ChonkPlugin : IMeowsPlugin
{
    public string Id => "meows.chonk";

    public string DisplayName => "Chonk";

    public string Description =>
        "Measures where the room on a drive went, biggest first, and clears out what you no longer want.";

    public string Icon => "🐈";

    public string Category => "Disk and tidying";

    public Control CreateView(IMeowsHost host) => new ChonkView
    {
        DataContext = new ChonkViewModel(host),
    };
}
