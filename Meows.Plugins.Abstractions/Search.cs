namespace Meows.Plugins.Abstractions;

/// <summary>
/// One thing a plugin found for the command palette: a file in Kibble's grid, a date in Collar,
/// a set in Purrge. Enter on it brings the plugin's tab to the front and runs <see cref="Open"/>,
/// which should select or reveal the thing so the eye lands on it.
/// </summary>
public sealed record SearchHit(string Title, string Detail, Action Open);

/// <summary>
/// Implemented by a plugin's view model to be searched from Ctrl+K. The palette already finds
/// tabs, settings and history; this is the half that reaches into what each open plugin is
/// showing. Only plugins that are switched on are asked, since there is nothing loaded to
/// search in one that is not.
///
/// Called on the UI thread on every keystroke while the palette is open, so answer from what is
/// already in memory and never touch the disk or the network here.
/// </summary>
public interface ISearchable
{
    /// <summary>
    /// Matches for the words typed, best first, at most <paramref name="limit"/>. The query is
    /// trimmed and at least two characters long. <see cref="SearchWords.Match"/> does the usual
    /// every-word-somewhere test.
    /// </summary>
    IReadOnlyList<SearchHit> Search(string query, int limit);
}

/// <summary>The one matching rule, so every plugin's search answers the same way the palette ranks.</summary>
public static class SearchWords
{
    public static string[] Split(string query) =>
        query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Whether every word appears in at least one of the texts, case-insensitively.</summary>
    public static bool Match(string[] words, params string?[] texts)
    {
        foreach (var word in words)
        {
            var found = false;
            foreach (var text in texts)
            {
                if (text is not null && text.Contains(word, StringComparison.CurrentCultureIgnoreCase))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
                return false;
        }

        return true;
    }
}
