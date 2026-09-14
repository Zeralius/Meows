using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using Meows.Plugins.Abstractions;

namespace Meows.Services;

/// <summary>A running task, as the Tasks panel shows it.</summary>
public sealed class BackgroundTaskItem : INotifyPropertyChanged, IBackgroundTask, IBackgroundContext
{
    private readonly CancellationTokenSource _cts;
    private readonly Action<BackgroundTaskItem> _onFinished;
    private string _status = "";
    private double? _progress;
    private bool _isRunning = true;

    internal BackgroundTaskItem(string source, string title, CancellationToken parent,
        Action<BackgroundTaskItem> onFinished)
    {
        Source = source;
        Title = title;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
        _onFinished = onFinished;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Source { get; }

    /// <summary>The id of the plugin that started it, which is the key its watches are cleared by.</summary>
    public string PluginId { get; internal init; } = "";

    /// <summary>The name to show, which follows the feline switch.</summary>
    public string SourceName => PluginNames.Display(Source);

    public string Title { get; }

    public CancellationToken Token => _cts.Token;

    // ---- what a schedule keeps about itself, read by Purr through IMeowsWatches ----

    /// <summary>Null for a one-off task. A schedule has one and is listed as a watch.</summary>
    public TimeSpan? Interval { get; internal init; }

    public DateTime StartedAt { get; } = DateTime.Now;

    public DateTime? LastPassAt { get; private set; }

    public int Passes { get; private set; }

    public bool PassInProgress { get; private set; }

    public DateTime? StoppedAt { get; private set; }

    public string? LastFailure { get; private set; }

    public DateTime? NextDueAt => Interval is { } every && StoppedAt is null && !PassInProgress
        ? (LastPassAt ?? StartedAt) + every
        : null;

    internal void PassStarted() => PassInProgress = true;

    internal void PassFinished()
    {
        PassInProgress = false;
        LastPassAt = DateTime.Now;
        Passes++;
    }

    internal void Stopped(string? failure)
    {
        PassInProgress = false;
        StoppedAt = DateTime.Now;
        LastFailure = failure;
    }

    public WatchInfo AsWatch() => new(
        PluginId, SourceName, Title, Interval ?? TimeSpan.Zero, StartedAt, LastPassAt, NextDueAt,
        Passes, PassInProgress, StoppedAt, LastFailure, Status);

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public double? Progress
    {
        get => _progress;
        private set
        {
            if (Set(ref _progress, value))
            {
                OnPropertyChanged(nameof(HasProgress));
                OnPropertyChanged(nameof(ProgressPercent));
            }
        }
    }

    public bool HasProgress => _progress is not null;

    public int ProgressPercent => (int)Math.Round((_progress ?? 0) * 100);

    public bool IsRunning
    {
        get => _isRunning;
        private set => Set(ref _isRunning, value);
    }

    public void Report(string status) => OnUiThread(() => Status = status);

    public void ReportProgress(double? fraction) =>
        OnUiThread(() => Progress = fraction is null ? null : Math.Clamp(fraction.Value, 0, 1));

    public void Cancel()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished and disposed.
        }
    }

    internal void MarkFinished()
    {
        OnUiThread(() => IsRunning = false);
        _onFinished(this);
    }

    public void Dispose()
    {
        Cancel();
        _cts.Dispose();
    }

    private static void OnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Holds every plugin's background work. The shell owns the lifetime so nothing keeps running
/// after a plugin goes away, and a fault turns into a notification instead of an unobserved
/// task exception nobody ever sees.
/// </summary>
public sealed class BackgroundTaskService : IDisposable
{
    private readonly NotificationCenter _notifications;
    private readonly ShellLog _log;
    private readonly CancellationTokenSource _appShutdown = new();
    private readonly Dictionary<string, CancellationTokenSource> _perPlugin = new(StringComparer.OrdinalIgnoreCase);

    public BackgroundTaskService(NotificationCenter notifications, ShellLog log)
    {
        _notifications = notifications;
        _log = log;
    }

    public ObservableCollection<BackgroundTaskItem> Running { get; } = new();

    /// <summary>
    /// What a failed task's notification offers: the window sets this to "switch the plugin off
    /// and on", which is the only way a stopped schedule starts again. Null for a plugin the
    /// window cannot restart, the shell's own included.
    /// </summary>
    public Func<string, NotificationAction?>? RestartActionFor { get; set; }

    /// <summary>
    /// Every schedule, kept after it ends so a watch that failed can still be seen to have
    /// failed. Cleared for a plugin when it is switched off, since its watches went with it.
    /// </summary>
    private readonly List<BackgroundTaskItem> _watches = [];

    public event Action? Changed;

    /// <summary>Raised on the UI thread whenever a watch starts, passes, fails or ends.</summary>
    public event Action? WatchesChanged;

    public IReadOnlyList<WatchInfo> Watches()
    {
        lock (_watches)
            return _watches.Select(w => w.AsWatch()).ToList();
    }

    private void WatchesMoved() => OnUiThread(() => WatchesChanged?.Invoke());

    public int RunningCount => Running.Count;

    internal CancellationToken TokenFor(string pluginId)
    {
        lock (_perPlugin)
        {
            if (!_perPlugin.TryGetValue(pluginId, out var cts) || cts.IsCancellationRequested)
                _perPlugin[pluginId] = cts = CancellationTokenSource.CreateLinkedTokenSource(_appShutdown.Token);
            return cts.Token;
        }
    }

    /// <summary>Deactivation. Everything that plugin registered stops here.</summary>
    public void CancelAllFor(string pluginId)
    {
        lock (_watches)
            _watches.RemoveAll(w => string.Equals(w.PluginId, pluginId, StringComparison.OrdinalIgnoreCase));
        WatchesMoved();

        CancellationTokenSource? cts;
        lock (_perPlugin)
        {
            if (!_perPlugin.Remove(pluginId, out cts))
                return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed, nothing to cancel.
        }
        finally
        {
            cts.Dispose();
        }
    }

    /// <summary>
    /// The shell's own schedules, kept and shown like a plugin's. Purr lists them under "Meows",
    /// which is where the housekeeping belongs to be seen.
    /// </summary>
    public IBackgroundTask ScheduleForShell(string title, TimeSpan interval, Func<IBackgroundContext, Task> work, bool runImmediately) =>
        Start("meows.shell", "Meows", title, work, interval, runImmediately);

    internal BackgroundTaskItem Start(string pluginId, string source, string title,
        Func<IBackgroundContext, Task> work, TimeSpan? interval, bool runImmediately)
    {
        var item = new BackgroundTaskItem(source, title, TokenFor(pluginId), Remove)
        {
            PluginId = pluginId,
            Interval = interval,
        };
        Add(item);
        if (interval is not null)
        {
            lock (_watches)
                _watches.Add(item);
            WatchesMoved();
        }

        _ = Task.Run(async () =>
        {
            string? failure = null;
            try
            {
                if (interval is null)
                {
                    await work(item).ConfigureAwait(false);
                }
                else
                {
                    if (!runImmediately)
                        await Task.Delay(interval.Value, item.Token).ConfigureAwait(false);

                    while (!item.Token.IsCancellationRequested)
                    {
                        item.PassStarted();
                        await work(item).ConfigureAwait(false);
                        item.PassFinished();
                        WatchesMoved();

                        // Delay after the pass, not on a fixed clock, so a slow run pushes
                        // the next one back instead of two running at once.
                        await Task.Delay(interval.Value, item.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Deactivation and shutdown both land here. Nothing to report.
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                _log.Write(source, $"Background task '{title}' failed: {ex}", LogLevel.Error);
                _notifications.Post(source, NotificationSeverity.Error,
                    $"{title} failed", ex.Message, RestartActionFor?.Invoke(pluginId));
            }
            finally
            {
                if (interval is not null)
                {
                    item.Stopped(failure);
                    WatchesMoved();
                }
                item.MarkFinished();
            }
        });

        return item;
    }

    private void Add(BackgroundTaskItem item) => OnUiThread(() =>
    {
        Running.Add(item);
        Raise();
    });

    private void Remove(BackgroundTaskItem item) => OnUiThread(() =>
    {
        Running.Remove(item);
        Raise();
    });

    private void Raise()
    {
        Changed?.Invoke();
    }

    private static void OnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    public void Dispose()
    {
        try
        {
            _appShutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already cancelled.
        }

        lock (_perPlugin)
        {
            foreach (var cts in _perPlugin.Values)
                cts.Dispose();
            _perPlugin.Clear();
        }

        _appShutdown.Dispose();
    }
}

/// <summary>What a plugin actually gets, tied to its own cancellation scope.</summary>
public sealed class PluginBackgroundWork : IMeowsBackgroundWork
{
    private readonly BackgroundTaskService _service;
    private readonly string _pluginId;
    private readonly string _source;

    public PluginBackgroundWork(BackgroundTaskService service, string pluginId, string source)
    {
        _service = service;
        _pluginId = pluginId;
        _source = source;
    }

    public IBackgroundTask Run(string title, Func<IBackgroundContext, Task> work) =>
        _service.Start(_pluginId, _source, title, work, interval: null, runImmediately: true);

    public IBackgroundTask Schedule(string title, TimeSpan interval, Func<IBackgroundContext, Task> work,
        bool runImmediately = true) =>
        _service.Start(_pluginId, _source, title, work, interval, runImmediately);
}
