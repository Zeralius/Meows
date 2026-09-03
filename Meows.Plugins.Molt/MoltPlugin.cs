using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Molt.ViewModels;
using Meows.Plugins.Molt.Views;

namespace Meows.Plugins.Molt;

public sealed class MoltPlugin : IMeowsPlugin
{
    public string Id => "meows.molt";

    public string DisplayName => "Molt";

    public string Description => "molt.description";

    public string Icon => "🍂";

    public string Category => "group.disk";

    public Control CreateView(IMeowsHost host) => new MoltView
    {
        DataContext = new MoltViewModel(host),
    };
}
