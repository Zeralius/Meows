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

    public string Title { get; }

    public CancellationToken Token => _cts.Token;

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

    public event Action? Changed;

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

    internal BackgroundTaskItem Start(string pluginId, string source, string title,
        Func<IBackgroundContext, Task> work, TimeSpan? interval, bool runImmediately)
    {
        var item = new BackgroundTaskItem(source, title, TokenFor(pluginId), Remove);
        Add(item);

        _ = Task.Run(async () =>
        {
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
                        await work(item).ConfigureAwait(false);
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
                _log.Write(source, $"Background task '{title}' failed: {ex}");
                _notifications.Post(source, NotificationSeverity.Error,
                    $"{title} failed", ex.Message, action: null);
            }
            finally
            {
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
