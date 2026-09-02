using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Molt.ViewModels;
using Meows.Plugins.Molt.Views;

namespace Meows.Plugins.Molt;

public sealed class MoltPlugin : IMeowsPlugin
{
    public string Id => "meows.molt";

    public string DisplayName => "Molt";

    public string Description =>
        "Sheds the caches and build output that can be rebuilt, and says what losing each one costs before you do it.";

    public string Icon => "🍂";

    public string Category => "Disk and tidying";

    public Control CreateView(IMeowsHost host) => new MoltView
    {
        DataContext = new MoltViewModel(host),
    };
}
