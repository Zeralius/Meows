using Avalonia.Controls;
using Mews.Plugins.Abstractions;
using Mews.Plugins.Purrge.ViewModels;
using Mews.Plugins.Purrge.Views;

namespace Mews.Plugins.Purrge;

public sealed class PurrgePlugin : IMewsPlugin
{
    public string Id => "mews.purrge";

    public string DisplayName => "Purrge";

    public string Description =>
        "Finds files with identical content anywhere on the machine, groups them, and clears out the copies you do not want.";

    public string Icon => "🐾";

    public string Category => "Disk and tidying";

    public Control CreateView(IMewsHost host) => new PurrgeView
    {
        DataContext = new PurrgeViewModel(host),
    };
}
