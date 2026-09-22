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

    /// <summary>A handoff Familiar sends itself: open this kit. The note carries the kit's folder.</summary>
    public const string KitVerb = "familiar.kit";

    public Control CreateView(IMeowsHost host) => new FamiliarView
    {
        DataContext = new FamiliarViewModel(host),
    };

    /// <summary>
    /// The one-shots are folders under the root in the settings, so Ctrl+K can reach them by
    /// name while Familiar is off: one listing when first asked, and a hit switches Familiar on
    /// and opens the kit. Pictures inside a kit wait until it is on.
    /// </summary>
    public ISearchable? WhileOff(IMeowsDormantHost host)
    {
        var root = host.LoadSettings<FamiliarSettings>()?.Root;
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            return null;

        List<string> kits;
        try
        {
            kits = Directory.EnumerateDirectories(root)
                .Where(f => Path.GetFileName(f) is var name
                    && !name.Equals("Exports", StringComparison.OrdinalIgnoreCase)
                    && !name.Equals(Services.FrameSet.UserFolderName, StringComparison.OrdinalIgnoreCase)
                    && !name.Equals(FamiliarViewModel.BenchFolderName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            return null;
        }

        return kits.Count == 0 ? null : new SleepingFamiliar(host, kits);
    }

    private sealed class SleepingFamiliar(IMeowsDormantHost host, IReadOnlyList<string> kits) : ISearchable
    {
        public IReadOnlyList<SearchHit> Search(string query, int limit)
        {
            var words = SearchWords.Split(query);
            var hits = new List<SearchHit>();
            foreach (var folder in kits)
            {
                if (hits.Count >= limit)
                    break;
                var name = Path.GetFileName(folder);
                if (!SearchWords.Match(words, name))
                    continue;
                var chosen = folder;
                hits.Add(new SearchHit(name, host.Text["familiar.kit.detail"], () => host.Handoff.Send(host.PluginId, new Handoff(KitVerb, [], chosen))));
            }
            return hits;
        }
    }
}
