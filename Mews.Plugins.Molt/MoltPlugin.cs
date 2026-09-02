using Avalonia.Controls;
using Mews.Plugins.Abstractions;
using Mews.Plugins.Molt.ViewModels;
using Mews.Plugins.Molt.Views;

namespace Mews.Plugins.Molt;

public sealed class MoltPlugin : IMewsPlugin
{
    public string Id => "mews.molt";

    public string DisplayName => "Molt";

    public string Description =>
        "Sheds the caches and build output that can be rebuilt, and says what losing each one costs before you do it.";

    public string Icon => "🍂";

    public string Category => "Disk and tidying";

    public Control CreateView(IMewsHost host) => new MoltView
    {
        DataContext = new MoltViewModel(host),
    };
}
