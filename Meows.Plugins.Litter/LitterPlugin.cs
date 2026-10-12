using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Catnip;
using Meows.Plugins.Litter.ViewModels;
using Meows.Plugins.Litter.Views;
using Meows.Plugins.Mouser;

namespace Meows.Plugins.Litter;

/// <summary>
/// Clutter, in three pages: the downloads folder as Litter always read it, what was downloaded
/// and never opened (Catnip until 6.1.0), and what is left on its own, empty folders, dead
/// shortcuts (Mouser until 6.1.0). Three tabs that all asked "what here can go?" are one now.
/// Litter keeps its id, so its settings and history carry straight on; the other two read
/// theirs from folders of their own under Litter's.
/// </summary>
public sealed class LitterPlugin() : PagedPlugin(new DownloadsPage(), new CatnipPlugin(), new MouserPlugin())
{
    public override string PlainName => "litter.name.plain";

    public override string Description => "litter.description";
}

/// <summary>The first page: the downloads folder, as Litter has always read it.</summary>
internal sealed class DownloadsPage : IMeowsPlugin
{
    public string Id => "meows.litter";

    public string DisplayName => "Litter";

    public string PlainName => "litter.page.plain";

    public string Description => "litter.page.description";

    public string Icon => "🧺";

    public string Category => "group.disk";

    public PluginTopics Topics => PluginTopics.Files;

    public Control CreateView(IMeowsHost host) => new LitterView
    {
        DataContext = new LitterViewModel(host),
    };
}
