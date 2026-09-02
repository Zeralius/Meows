using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Mouser.ViewModels;
using Meows.Plugins.Mouser.Views;

namespace Meows.Plugins.Mouser;

public sealed class MouserPlugin : IMeowsPlugin
{
    public string Id => "meows.mouser";

    public string DisplayName => "Mouser";

    public string Description =>
        "Hunts down dead weight: empty folders, empty files, shortcuts pointing at things that are gone, and the leftovers a file browser scatters about.";

    public string Icon => "🐁";

    public string Category => "Disk and tidying";

    public Control CreateView(IMeowsHost host) => new MouserView
    {
        DataContext = new MouserViewModel(host),
    };
}
