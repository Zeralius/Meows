using System.Collections.Concurrent;
using Meows.Plugins.Abstractions;

namespace Meows.Services;

/// <summary>What one plugin has cost since Meows started: how long it took to open, and the background work it asked for.</summary>
/// <param name="Opened">How long creating its tab took, the part of startup it is responsible for. Null when it has not been opened.</param>
/// <param name="Runs">Background runs and scheduled passes finished.</param>
/// <param name="Busy">The time those runs and passes took, added up.</param>
/// <param name="Longest">The single longest of them.</param>
public sealed record PluginCost(TimeSpan? Opened, int Runs, TimeSpan Busy, TimeSpan Longest, int Failures)
{
    public static readonly PluginCost Nothing = new(null, 0, TimeSpan.Zero, TimeSpan.Zero, 0);

    /// <summary>Opening took long enough to notice: past half a second, which a person waiting for the window feels.</summary>
    public bool SlowToOpen => Opened > PluginCosts.SlowOpen;
}

/// <summary>
/// Plugin cost, kept for this run of Meows: how long each plugin's tab took to open and how much
/// background work it did. Memory is not here on purpose: every plugin shares one process and one
/// heap, and a number that cannot be attributed honestly is worse than none. The time is honest:
/// it is measured around the plugin's own code.
/// </summary>
public sealed class PluginCosts
{
    public static readonly TimeSpan SlowOpen = TimeSpan.FromMilliseconds(500);

    private readonly ConcurrentDictionary<string, PluginCost> _costs = new(StringComparer.OrdinalIgnoreCase);

    public void Opened(string pluginId, TimeSpan took) =>
        _costs.AddOrUpdate(pluginId, _ => PluginCost.Nothing with { Opened = took }, (_, c) => c with { Opened = took });

    public void Ran(string pluginId, TimeSpan took, bool failed)
    {
        if (pluginId.Length == 0)
            return;
        _costs.AddOrUpdate(pluginId,
            _ => new PluginCost(null, 1, took, took, failed ? 1 : 0),
            (_, c) => c with
            {
                Runs = c.Runs + 1,
                Busy = c.Busy + took,
                Longest = took > c.Longest ? took : c.Longest,
                Failures = c.Failures + (failed ? 1 : 0),
            });
    }

    public PluginCost For(string pluginId) => _costs.TryGetValue(pluginId, out var cost) ? cost : PluginCost.Nothing;

    /// <summary>The card's line: "opened in 120 ms · 14 runs, 3 min busy, the longest 2 min". Empty when there is nothing to say.</summary>
    public static string Describe(PluginCost cost, IMeowsText text)
    {
        var parts = new List<string>();
        if (cost.Opened is { } opened)
            parts.Add(text.Format(cost.SlowToOpen ? "cost.opened.slow" : "cost.opened", Duration(opened, text)));
        if (cost.Runs > 0)
        {
            parts.Add(cost.Runs == 1
                ? text.Format("cost.runs.one", Duration(cost.Busy, text))
                : text.Format("cost.runs", cost.Runs, Duration(cost.Busy, text), Duration(cost.Longest, text)));
        }
        if (cost.Failures > 0)
            parts.Add(text.Format("cost.failures", cost.Failures));
        return string.Join(" · ", parts);
    }

    /// <summary>A length of time in the one or two units that read naturally: 45 ms, 3.2 s, 4 min 10 s, 2 h 5 min.</summary>
    public static string Duration(TimeSpan span, IMeowsText text) => span.TotalMilliseconds switch
    {
        < 1000 => text.Format("cost.ms", (int)Math.Round(span.TotalMilliseconds)),
        < 60_000 => text.Format("cost.s", span.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture)),
        < 3_600_000 => text.Format("cost.min", (int)span.TotalMinutes, span.Seconds),
        _ => text.Format("cost.h", (int)span.TotalHours, span.Minutes),
    };
}
