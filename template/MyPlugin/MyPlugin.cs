using Avalonia.Controls;
using Mews.Plugins.Abstractions;

namespace MyPlugin;

public sealed class MyPluginPlugin : IMewsPlugin
{
    /// <summary>Stable forever. It is the settings key and the activation record.</summary>
    public string Id => "PLUGIN-ID";

    public string DisplayName => "MyPlugin";

    public string Description => "One sentence, shown on the Plugins tab.";

    public string? Icon => "🎲";

    /// <summary>Heading on the Plugins tab. Null puts it with everything else.</summary>
    public string? Category => "PLUGIN-CATEGORY";

    public Control CreateView(IMewsHost host) => new MyPluginView
    {
        DataContext = new MyPluginViewModel(host),
    };
}
