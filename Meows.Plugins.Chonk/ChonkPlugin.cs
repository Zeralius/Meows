using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Chonk.ViewModels;
using Meows.Plugins.Chonk.Views;
using Meows.Plugins.WeighIn;

namespace Meows.Plugins.Chonk;

/// <summary>
/// Disk usage, now and over time: where the room on a drive went, and, on its second page, how
/// each drive has grown day by day (Weigh-In until 6.1.0). Weigh-In's "show me that folder" was
/// already a handoff to Chonk; now it lands on the page beside it. Chonk keeps its id, so its
/// settings and history carry straight on; Weigh-In reads its readings from a folder under Chonk's.
/// </summary>
public sealed class ChonkPlugin() : PagedPlugin(new MeasurePage(), new WeighInPlugin())
{
    public override string PlainName => "chonk.name.plain";

    public override string Description => "chonk.description";
}

/// <summary>The first page: one drive or folder measured right now, as Chonk has always done.</summary>
internal sealed class MeasurePage : IMeowsPlugin
{
    public string Id => "meows.chonk";

    public string DisplayName => "Chonk";

    public string PlainName => "chonk.page.plain";

    public string Description => "chonk.page.description";

    public string Icon => "🐈";

    public string Category => "group.disk";

    public PluginTopics Topics => PluginTopics.Files;

    public IReadOnlyList<RecordedKind> Records =>
    [
        new("extracted", "chonk.records.extracted"),
        new("recycled", "chonk.records.recycled"),
    ];

    public Control CreateView(IMeowsHost host) => new ChonkView
    {
        DataContext = new ChonkViewModel(host),
    };
}
