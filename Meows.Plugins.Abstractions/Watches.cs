namespace Meows.Plugins.Abstractions;

/// <summary>
/// One thing some plugin has asked the shell to do on a schedule, and how it has been going:
/// Collar's daily look at its dates, Birdwatch's refresh, Saucer's clipboard poll.
/// </summary>
/// <param name="PluginId">Whose it is.</param>
/// <param name="PluginName">That plugin's name as it shows right now.</param>
/// <param name="Title">What the plugin called it, written for a person.</param>
/// <param name="Interval">How long it waits after each pass.</param>
/// <param name="StartedAt">When the schedule was registered, which is usually when the plugin was switched on.</param>
/// <param name="LastPassAt">When the last pass finished, or null before the first.</param>
/// <param name="NextDueAt">When the next pass is expected, or null while one is in progress or after it stopped.</param>
/// <param name="Passes">How many passes have completed.</param>
/// <param name="PassInProgress">Whether a pass is running at this moment.</param>
/// <param name="StoppedAt">When the schedule ended, if it has: a pass threw, or the plugin was switched off.</param>
/// <param name="LastFailure">The message of the pass that ended it, or null.</param>
/// <param name="Status">The last thing the pass reported, or empty.</param>
public sealed record WatchInfo(
    string PluginId,
    string PluginName,
    string Title,
    TimeSpan Interval,
    DateTime StartedAt,
    DateTime? LastPassAt,
    DateTime? NextDueAt,
    int Passes,
    bool PassInProgress,
    DateTime? StoppedAt,
    string? LastFailure,
    string Status)
{
    public bool IsStopped => StoppedAt is not null;

    /// <summary>
    /// What to hand back to <see cref="IMeowsWatches.Pause"/> and <see cref="IMeowsWatches.Resume"/>.
    /// Stable for as long as the schedule runs; empty from a shell before 0.10.0. Since 0.10.0.
    /// </summary>
    public string Id { get; init; } = "";

    /// <summary>
    /// Not looking until then: someone asked for quiet. <see cref="DateTime.MaxValue"/> for
    /// "until I say so"; null when it is not paused. The schedule is kept, nothing is lost, and
    /// the first pass after the pause runs at once. Since 0.10.0.
    /// </summary>
    public DateTime? PausedUntil { get; init; }

    public bool IsPaused => PausedUntil is { } until && until > DateTime.Now;
}

/// <summary>
/// Every schedule the shell is running for every plugin, read-only, for whoever wants to say
/// what Meows is watching and when it last looked. Not scoped to the asking plugin on purpose:
/// the one plugin that wants this wants it across all of them.
/// </summary>
public interface IMeowsWatches
{
    IReadOnlyList<WatchInfo> All();

    /// <summary>Raised on the UI thread when a watch starts, passes, fails or ends.</summary>
    event Action? Changed;

    /// <summary>
    /// Holds a watch until a moment (<see cref="DateTime.MaxValue"/> for indefinitely). The
    /// plugin's schedule stays registered; passes simply do not run. False when the shell is
    /// older than 0.10.0 or the id is not a running watch. Since 0.10.0.
    /// </summary>
    bool Pause(string id, DateTime until) => false;

    /// <summary>Lets a paused watch look again, at once. Since 0.10.0.</summary>
    bool Resume(string id) => false;
}

/// <summary>What a shell built against an older contract answers. Nothing is watched.</summary>
public sealed class NoWatches : IMeowsWatches
{
    public static NoWatches Instance { get; } = new();

    public IReadOnlyList<WatchInfo> All() => [];

    public event Action? Changed
    {
        add { }
        remove { }
    }
}
