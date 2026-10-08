using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Carry.ViewModels;
using Meows.Plugins.Carry.Views;

namespace Meows.Plugins.Carry;

public sealed class CarryPlugin : IMeowsPlugin
{
    /// <summary>Stable forever. It is the settings key and the activation record.</summary>
    public string Id => "meows.carry";

    public string DisplayName => "Carry";

    public string PlainName => "carry.name.plain";

    public string Description => "carry.description";

    public string Icon => "🧳";

    public string Category => "group.disk";

    public IReadOnlyList<RecordedKind> Records =>
    [
        new("carried", "carry.records.carried"),
        new("broughtback", "carry.records.broughtback"),
    ];

    public Control CreateView(IMeowsHost host) => new CarryView
    {
        DataContext = new CarryViewModel(host),
    };
}
