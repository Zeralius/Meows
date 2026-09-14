using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Familiar.ViewModels;
using Meows.Plugins.Familiar.Views;

namespace Meows.Plugins.Familiar;

public sealed class FamiliarPlugin : IMeowsPlugin
{
    /// <summary>Stable forever. It is the settings key and the activation record.</summary>
    public string Id => "meows.familiar";

    public string DisplayName => "Familiar";

    public string PlainName => "familiar.name.plain";

    public string Description => "familiar.description";

    public string Icon => "🧺";

    public string Category => "group.everyday";

    public Control CreateView(IMeowsHost host) => new FamiliarView
    {
        DataContext = new FamiliarViewModel(host),
    };
}
