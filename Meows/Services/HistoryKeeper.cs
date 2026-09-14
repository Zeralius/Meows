using Meows.Plugins.Abstractions;

namespace Meows.Services;

/// <summary>
/// Applies the one rule the Settings tab lets someone set about the history: how long a line
/// is kept. <see cref="Apply"/> runs once at startup, on the thread that built it and before any
/// plugin is writing; then a daily schedule the shell owns takes over, so Purr shows it under
/// Meows like any other watch. Zero days means forever and the keeper does nothing at all.
/// </summary>
public sealed class HistoryKeeper : IDisposable
{
    private readonly MeowsStore _store;
    private readonly Func<int> _keepDays;
    private readonly ShellLog _log;
    private IBackgroundTask? _daily;

    public HistoryKeeper(MeowsStore store, Func<int> keepDays, ShellLog log, BackgroundTaskService background, IMeowsText text)
    {
        _store = store;
        _keepDays = keepDays;
        _log = log;

        _daily = background.ScheduleForShell(text["history.keeper.task"], TimeSpan.FromHours(24), context =>
        {
            var gone = Apply();
            context.Report(gone == 0 ? text["history.keeper.nothing"] : text.Format("history.keeper.forgot", gone));
            return Task.CompletedTask;
        }, runImmediately: false);
    }

    /// <summary>Forgets what the rule says to forget, and says how many lines went. Safe to call any time.</summary>
    public long Apply()
    {
        var days = _keepDays();
        if (days <= 0)
            return 0;

        var cutoff = DateTime.UtcNow.AddDays(-days);
        var gone = _store.Forget(cutoff);
        if (gone > 0)
        {
            _store.Compact();
            _log.Write("store", $"Kept {days} days of history: forgot {gone} older line(s).");
        }

        return gone;
    }

    public void Dispose()
    {
        _daily?.Cancel();
        _daily = null;
    }
}
