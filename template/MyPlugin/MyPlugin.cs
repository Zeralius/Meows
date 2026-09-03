using Avalonia.Controls;
using Meows.Plugins.Abstractions;

namespace MyPlugin;

public sealed class MyPluginPlugin : IMeowsPlugin
{
    /// <summary>Stable forever. It is the settings key and the activation record.</summary>
    public string Id => "PLUGIN-ID";

    public string DisplayName => "MyPlugin";

    /// <summary>
    /// A key from the Strings folder, so the card reads in whatever language the window is in.
    /// A plain sentence works too and is shown exactly as written.
    /// </summary>
    public string Description => "PLUGIN-ID.description";

    public string? Icon => "🎲";

    /// <summary>
    /// Heading on the Plugins tab. Null puts it with everything else. The built-in headings are
    /// keys, so returning one of those joins that group and gets translated with it; any other
    /// text makes a group of its own and is shown as written.
    /// </summary>
    public string? Category => "PLUGIN-CATEGORY";

    public Control CreateView(IMeowsHost host) => new MyPluginView
    {
        DataContext = new MyPluginViewModel(host),
    };
}
