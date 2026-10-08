using Meows.Plugins.Abstractions;

namespace Meows.Services;

/// <summary>A week of the history, counted.</summary>
/// <param name="Things">Everything recorded.</param>
/// <param name="Recycled">Bytes sent to the Recycle Bin, from the size recycled lines carry.</param>
/// <param name="Carried">Bytes Carry moved off their drives.</param>
/// <param name="RulesFired">Lines a rule set off, from the stamp Instinct puts on them.</param>
/// <param name="ByKind">What each plugin did, most first.</param>
public sealed record Recap(
    DateTime FromUtc,
    DateTime ToUtc,
    int Things,
    long Recycled,
    long Carried,
    int RulesFired,
    IReadOnlyList<(string Plugin, string Kind, int Count)> ByKind);

/// <summary>
/// The weekly recap: a week of the history, counted, on Home and as one notification a week. It
/// owns nothing: every number is read back from what the plugins already recorded, so it is only
/// ever as true as the history, and never adds a line of its own to it.
/// </summary>
public static class WeeklyRecap
{
    public static readonly TimeSpan Week = TimeSpan.FromDays(7);

    public static Recap Of(IReadOnlyList<StoredEvent> events, DateTime fromUtc, DateTime toUtc)
    {
        long recycled = 0, carried = 0;
        var rules = 0;
        foreach (var e in events)
        {
            if (e.Kind == "recycled" && e.Data.TryGetValue("size", out var size) && long.TryParse(size, out var bytes))
                recycled += bytes;
            if (e.Kind == "carried" && e.Data.TryGetValue("bytes", out var moved) && long.TryParse(moved, out var movedBytes))
                carried += movedBytes;
            if (e.Data.ContainsKey(InstinctStamp))
                rules++;
        }

        var byKind = events
            .GroupBy(e => (e.Plugin, e.Kind))
            .Select(g => (g.Key.Plugin, g.Key.Kind, Count: g.Count()))
            .OrderByDescending(k => k.Count)
            .ThenBy(k => k.Plugin, StringComparer.Ordinal)
            .ToList();

        return new Recap(fromUtc, toUtc, events.Count, recycled, carried, rules, byKind);
    }

    /// <summary>The key Instinct stamps on a line a rule set off.</summary>
    private const string InstinctStamp = "instinct.rule";

    /// <summary>
    /// Home's lines: the totals, then the five things done most, each in the words the plugin gave
    /// the Rules tab for that kind of line where it gave any.
    /// </summary>
    public static IReadOnlyList<string> Lines(Recap recap, Func<string, string> pluginName, Func<string, string, string?> kindLabel, IMeowsText text)
    {
        if (recap.Things == 0)
            return [text["recap.quiet"]];

        var lines = new List<string> { text.Format("recap.things", recap.Things) };
        if (recap.Recycled > 0)
            lines.Add(text.Format("recap.recycled", Humanise(recap.Recycled)));
        if (recap.Carried > 0)
            lines.Add(text.Format("recap.carried", Humanise(recap.Carried)));
        if (recap.RulesFired > 0)
            lines.Add(text.Format("recap.rules", recap.RulesFired));

        foreach (var (plugin, kind, count) in recap.ByKind.Take(5))
        {
            var said = kindLabel(plugin, kind) is { Length: > 0 } label ? text[label] : kind;
            lines.Add(text.Format("recap.did", pluginName(plugin), said, count));
        }
        return lines;
    }

    /// <summary>The notification's one sentence.</summary>
    public static string Summary(Recap recap, IMeowsText text)
    {
        if (recap.Things == 0)
            return text["recap.quiet"];
        var parts = new List<string> { text.Format("recap.things", recap.Things) };
        if (recap.Recycled > 0)
            parts.Add(text.Format("recap.recycled", Humanise(recap.Recycled)));
        if (recap.RulesFired > 0)
            parts.Add(text.Format("recap.rules", recap.RulesFired));
        return string.Join("; ", parts);
    }

    /// <summary>
    /// A week since the last one. The very first time there is no last one, and the answer is
    /// no: a recap of the week before Meows was set up would be a recap of nothing.
    /// </summary>
    public static bool IsDue(DateTime? lastUtc, DateTime nowUtc) => lastUtc is { } last && nowUtc - last >= Week;

    /// <summary>Sizes the way the plugins write them, in binary units, without the shell taking on the disk library for one line.</summary>
    public static string Humanise(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{size:0.#} {units[unit]}";
    }
}
