using Avalonia.Controls;
using Meows.Plugins.Abstractions;

namespace Yarn;

public sealed class YarnPlugin : IMeowsPlugin
{
    public string Id => "example.yarn";

    public string DisplayName => "Yarn";

    public string PlainName => "yarn.name.plain";

    public string Description => "yarn.description";

    public string? Icon => "🧶";

    public string? Category => "Examples";

    public Control CreateView(IMeowsHost host) => new YarnView
    {
        DataContext = new YarnViewModel(host),
    };

    /// <summary>
    /// Ctrl+K reaches a plugin that is off through this, once per scan, so what is kept is read
    /// here and searched from memory. A hit cannot select anything on a tab that does not
    /// exist; it hands the plugin to itself, and the shell switches it on and delivers.
    /// </summary>
    public ISearchable? WhileOff(IMeowsDormantHost host)
    {
        var settings = host.LoadSettings<YarnSettings>();
        return settings is null || settings.Threads.Count == 0 ? null : new Asleep(host, settings.Threads);
    }

    private sealed class Asleep(IMeowsDormantHost host, IReadOnlyList<string> threads) : ISearchable
    {
        public IReadOnlyList<SearchHit> Search(string query, int limit)
        {
            var words = SearchWords.Split(query);
            return threads
                .Where(t => SearchWords.Match(words, t))
                .Take(limit)
                .Select(t => new SearchHit(t, host.Text["yarn.hit.off"],
                    () => host.Handoff.Send(host.PluginId, new Handoff(YarnViewModel.ShowVerb, [], t))))
                .ToList();
        }
    }
}
