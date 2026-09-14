using Meows.Plugins.Abstractions;

namespace Meows.Services;

/// <summary>
/// The read-only view of every plugin's schedules, as a plugin sees it. Deliberately not scoped:
/// the one plugin that asks wants all of them.
/// </summary>
public sealed class WatchService(BackgroundTaskService background) : IMeowsWatches
{
    public IReadOnlyList<WatchInfo> All() => background.Watches();

    public bool Pause(string id, DateTime until) => background.PauseWatch(id, until);

    public bool Resume(string id) => background.ResumeWatch(id);

    public event Action? Changed
    {
        add => background.WatchesChanged += value;
        remove => background.WatchesChanged -= value;
    }
}
