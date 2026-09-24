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

    /// <summary>A rule's "take a reading now".</summary>
    public const string MeasureAction = "measure";

    public IReadOnlyList<PluginAction> Actions =>
    [
        new(MeasureAction, "weighin.action.measure", "weighin.action.measure.hint"),
    ];

    public IReadOnlyList<RecordedKind> Records =>
    [
        new("reading", "weighin.records.reading"),
    ];

    /// <summary>
    /// With no tab open there is no drive selected to tell the story of, so the line is when the
    /// last reading was, the same one the tab falls back to. The readings are small files; the
    /// drives themselves are never touched for this.
    /// </summary>
    public Glance? GlanceWhileOff(IMeowsDormantHost host) =>
        new(WeighInViewModel.SummaryOf(Services.Readings.Load(System.IO.Path.Combine(host.DataDirectory, "readings")), host.Text));

    public Control CreateView(IMeowsHost host) => new WeighInView
    {
        DataContext = new WeighInViewModel(host),
    };
}
