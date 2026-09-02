using Avalonia.Controls;
using Mews.Plugins.Abstractions;
using Mews.Plugins.Mouser.ViewModels;
using Mews.Plugins.Mouser.Views;

namespace Mews.Plugins.Mouser;

public sealed class MouserPlugin : IMewsPlugin
{
    public string Id => "mews.mouser";

    public string DisplayName => "Mouser";

    public string Description =>
        "Hunts down dead weight: empty folders, empty files, shortcuts pointing at things that are gone, and the leftovers a file browser scatters about.";

    public string Icon => "🐁";

    public string Category => "Disk and tidying";

    public Control CreateView(IMewsHost host) => new MouserView
    {
        DataContext = new MouserViewModel(host),
    };
}
