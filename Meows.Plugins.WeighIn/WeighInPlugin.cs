using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.WeighIn.ViewModels;
using Meows.Plugins.WeighIn.Views;

namespace Meows.Plugins.WeighIn;

public sealed class WeighInPlugin : IMeowsPlugin
{
    /// <summary>Stable forever. It is the settings key and the activation record.</summary>
    public string Id => "meows.weighin";

    public string DisplayName => "Weigh-In";

    public string PlainName => "weighin.name.plain";

    public string Description => "weighin.description";

    public string Icon => "⚖️";

    public string Category => "group.disk";

    public Control CreateView(IMeowsHost host) => new WeighInView
    {
        DataContext = new WeighInViewModel(host),
    };
}
