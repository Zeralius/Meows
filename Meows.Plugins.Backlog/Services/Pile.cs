using Meows.Disk;
using Meows.Plugins.Abstractions;

namespace Meows.Plugins.Backlog.Services;

/// <summary>Where a game stands. Shelved is a decision; done is finished with.</summary>
public enum BacklogStatus
{
    Backlog,
    Playing,
    Shelved,
    Done,
}

/// <summary>
/// One game on the pile, as it is stored.
///
/// A mutable class rather than a record, because this is what the settings file holds and it is
/// edited field by field on screen. Steam's own data (size, last played) is never stored: it is
/// read again on every scan and joined in memory, so the file stays small and honest.
/// </summary>
public sealed class BacklogEntry
{
    /// <summary>Steam's app id, or "manual:" plus a guid for a game added by hand.</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public BacklogStatus Status { get; set; } = BacklogStatus.Backlog;

    /// <summary>Zero means unrated. One to five once played enough to say.</summary>
    public int Rating { get; set; }

    public string Note { get; set; } = "";

    public bool IsManual => Id.StartsWith("manual:", StringComparison.Ordinal);
}

/// <summary>
/// The arithmetic behind the pile: what Steam added, what "pick for me" means, and the header.
///
/// Pure, so a test can hand it manifests and a settings list without a window.
/// </summary>
public static class Pile
{
    /// <summary>
    /// Folds a Steam read into the pile: unknown app ids arrive as backlog, known ones get their
    /// name refreshed. Nothing is ever removed here, not even games that are no longer
    /// installed: an uninstalled game is still a wish, and forgetting is a button, not a side effect.
    /// </summary>
    public static int EnsureFromSteam(List<BacklogEntry> entries, IReadOnlyList<SteamInstall> installs)
    {
        var added = 0;
        foreach (var install in installs)
        {
            var known = entries.FirstOrDefault(e => e.Id == install.AppId);
            if (known is null)
            {
                entries.Add(new BacklogEntry { Id = install.AppId, Name = install.Name });
                added++;
            }
            else if (known.Name != install.Name)
            {
                known.Name = install.Name;
            }
        }
        return added;
    }

    /// <summary>Candidates for tonight: what is waiting or already being played, finished and shelved aside.</summary>
    public static IReadOnlyList<BacklogEntry> Candidates(IEnumerable<BacklogEntry> entries) =>
        entries.Where(e => e.Status is BacklogStatus.Backlog or BacklogStatus.Playing).ToList();

    /// <summary>
    /// The weight behind "pick for me": an unrated game counts double because the pile exists to
    /// find out, a star counts once, and what is already being played counts one extra so tonight
    /// usually continues rather than restarts.
    /// </summary>
    public static int WeightOf(BacklogEntry entry) =>
        1 + (entry.Rating <= 0 ? 2 : Math.Clamp(entry.Rating, 1, 5)) + (entry.Status == BacklogStatus.Playing ? 1 : 0);

    /// <summary>One game for tonight, by weight. Null when there is nothing to play.</summary>
    public static BacklogEntry? Pick(IReadOnlyList<BacklogEntry> entries, Random random)
    {
        var candidates = Candidates(entries);
        if (candidates.Count == 0)
            return null;

        var total = candidates.Sum(WeightOf);
        var roll = random.Next(total);
        foreach (var candidate in candidates)
        {
            roll -= WeightOf(candidate);
            if (roll < 0)
                return candidate;
        }
        return candidates[^1];
    }

    /// <summary>The line under the header: what is waiting, and what is already finished.</summary>
    public static string SummaryOf(IReadOnlyList<BacklogEntry> entries, IMeowsText text)
    {
        if (entries.Count == 0)
            return "";

        var open = Candidates(entries).Count;
        var done = entries.Count(e => e.Status == BacklogStatus.Done);

        if (open > 0)
            return text.Format("backlog.summary.open", open, done);
        return text.Format("backlog.summary.clear", done);
    }

    /// <summary>The Home line from the pile alone. A fact, never trouble: nothing here is late.</summary>
    public static Glance? GlanceOf(IReadOnlyList<BacklogEntry> entries, IMeowsText text)
    {
        var summary = SummaryOf(entries, text);
        return summary.Length == 0 ? null : new Glance(summary);
    }

    /// <summary>Stars as text. Zero is unrated, which is not zero stars.</summary>
    public static string Stars(int rating, IMeowsText text) =>
        rating <= 0 ? text["backlog.rating.none"] : new string('★', Math.Clamp(rating, 1, 5));

    /// <summary>The key for a word rather than the word. Nothing here knows any language.</summary>
    public static string Describe(BacklogStatus status) => "backlog.status." + status.ToString().ToLowerInvariant();
}
