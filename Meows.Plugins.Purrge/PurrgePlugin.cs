using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Purrge.ViewModels;
using Meows.Plugins.Purrge.Views;

namespace Meows.Plugins.Purrge;

public sealed class PurrgePlugin : IMeowsPlugin
{
    public string Id => "meows.purrge";

    public string DisplayName => "Purrge";

    public string Description =>
        "Finds files with identical content anywhere on the machine, groups them, and clears out the copies you do not want.";

    public string Icon => "🐾";

    public string Category => "Disk and tidying";

    public Control CreateView(IMeowsHost host) => new PurrgeView
    {
        DataContext = new PurrgeViewModel(host),
    };
}
