using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Kibble.ViewModels;
using Meows.Plugins.Kibble.Views;

namespace Meows.Plugins.Kibble;

public sealed class KibblePlugin : IMeowsPlugin
{
    public string Id => "meows.kibble";

    public string DisplayName => "Kibble";

    public string PlainName => "kibble.name.plain";

    public string Description => "kibble.description";

    public string? Icon => "🍽";

    public string Category => "group.bot";

    /// <summary>A handoff Kibble sends itself: show this waiting file. The note carries the path.</summary>
    public const string ShowVerb = "kibble.show";

    public IReadOnlyList<RecordedKind> Records =>
    [
        new("sent", "kibble.records.sent"),
        new("set-aside", "kibble.records.set-aside"),
        new("held", "kibble.records.held"),
        new("shrunk", "kibble.records.shrunk"),
        new("undone", "kibble.records.undone"),
    ];

    public Control CreateView(IMeowsHost host) => new KibbleView
    {
        DataContext = new KibbleViewModel(host),
    };

    /// <summary>
    /// The queue is a folder, and the folder is in the settings, so Ctrl+K can reach what is
    /// waiting while Kibble is off: one listing of names when first asked, and a hit switches
    /// Kibble on and lands on the file. The destinations are not offered, as they are not when
    /// it is on: the only thing to do with one is send.
    /// </summary>
    public ISearchable? WhileOff(IMeowsDormantHost host)
    {
        var folder = host.LoadSettings<KibbleSettings>()?.LastSourceFolder;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return null;

        List<(string Name, string Path)> waiting;
        try
        {
            waiting = new DirectoryInfo(folder).EnumerateFiles().Select(f => (f.Name, f.FullName)).ToList();
        }
        catch (Exception)
        {
            return null;
        }

        return waiting.Count == 0 ? null : new SleepingKibble(host, Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)), waiting);
    }

    private sealed class SleepingKibble(IMeowsDormantHost host, string folderName, IReadOnlyList<(string Name, string Path)> waiting) : ISearchable
    {
        public IReadOnlyList<SearchHit> Search(string query, int limit)
        {
            var words = SearchWords.Split(query);
            var hits = new List<SearchHit>();
            foreach (var (name, path) in waiting)
            {
                if (hits.Count >= limit)
                    break;
                if (!SearchWords.Match(words, name))
                    continue;
                var chosen = path;
                hits.Add(new SearchHit(name, folderName, () => host.Handoff.Send(host.PluginId, new Handoff(ShowVerb, [], chosen))));
            }
            return hits;
        }
    }
}
