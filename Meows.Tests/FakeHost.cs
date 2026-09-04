using System.Text.Json;
using Meows.Plugins.Abstractions;

namespace Meows.Tests;

/// <summary>
/// Enough of the shell to construct a plugin view model in a test. Settings round-trip through
/// JSON rather than coming back as the same instance, so a view model that mutates what it was
/// given cannot pass by accident.
/// </summary>
public sealed class FakeHost : IMeowsHost
{
    private string? _settings;

    public FakeHost(string dataDirectory)
    {
        DataDirectory = dataDirectory;
        Directory.CreateDirectory(dataDirectory);
    }

    public string PluginId => "meows.test";

    public string DataDirectory { get; }

    public List<string> Lines { get; } = [];

    public Dictionary<string, string> Conditions { get; } = [];

    public IMeowsNotifications Notifications => new FakeNotifications(this);

    public IMeowsBackgroundWork Background { get; } = new FakeBackgroundWork();

    /// <summary>The same thing, typed, so a test can read what was asked for.</summary>
    public FakeBackgroundWork Work => (FakeBackgroundWork)Background;

    public void Log(string message) => Lines.Add(message);

    public T? LoadSettings<T>() where T : class =>
        _settings is null ? null : JsonSerializer.Deserialize<T>(_settings);

    public void SaveSettings<T>(T settings) where T : class =>
        _settings = JsonSerializer.Serialize(settings);

    /// <summary>
    /// Records work without running it. A view model that starts a scan in its constructor
    /// should not pull a real filesystem walk into every test that builds one; tests that care
    /// about results call the view model's own results method. This only records that work was
    /// requested.
    /// </summary>
    public sealed class FakeBackgroundWork : IMeowsBackgroundWork
    {
        /// <summary>One thing a plugin asked to have run on a timer.</summary>
        public sealed record ScheduledWork(
            string Title, TimeSpan Interval, Func<IBackgroundContext, Task> Work, bool RunImmediately)
        {
            public DeadTask Task { get; init; } = null!;
        }

        public List<string> Requested { get; } = [];

        /// <summary>
        /// Everything handed to Schedule, so a test can see how often a plugin asked to be run
        /// and drive one pass itself. Nothing runs on its own: a timer in a test is a way of
        /// making it slow and occasionally wrong.
        /// </summary>
        public List<ScheduledWork> Scheduled { get; } = [];

        public IBackgroundTask Run(string title, Func<IBackgroundContext, Task> work)
        {
            Requested.Add(title);
            return new DeadTask(title);
        }

        public IBackgroundTask Schedule(string title, TimeSpan interval, Func<IBackgroundContext, Task> work,
            bool runImmediately = true)
        {
            Requested.Add(title);
            var task = new DeadTask(title);
            Scheduled.Add(new ScheduledWork(title, interval, work, runImmediately) { Task = task });
            return task;
        }

        /// <summary>
        /// Runs one pass of the newest schedule with the work already called off, which is what
        /// the shell does to a timer when a plugin is switched off mid-wait.
        ///
        /// Only the called off case, and deliberately so. A pass that is allowed to proceed hops
        /// onto the UI thread, and this suite has no dispatcher running to hop onto, so it would
        /// wait for a loop that is never pumped. Asking for that here would hang rather than fail.
        /// </summary>
        public Task RunLatestCalledOffAsync()
        {
            using var stopped = new CancellationTokenSource();
            stopped.Cancel();
            return Scheduled[^1].Work(new DeadContext(stopped.Token));
        }

        public sealed class DeadTask(string title) : IBackgroundTask
        {
            public string Title { get; } = title;

            public bool IsRunning => !Cancelled;

            public bool Cancelled { get; private set; }

            public void Cancel() => Cancelled = true;

            public void Dispose() => Cancelled = true;
        }

        private sealed class DeadContext(CancellationToken token) : IBackgroundContext
        {
            public CancellationToken Token { get; } = token;

            public void Report(string status)
            {
            }

            public void ReportProgress(double? fraction)
            {
            }
        }
    }

    private sealed class FakeNotifications(FakeHost host) : IMeowsNotifications
    {
        public void Post(NotificationSeverity severity, string title, string message = "",
            NotificationAction? action = null) => host.Lines.Add($"post: {title}");

        public void SetCondition(string key, NotificationSeverity severity, string title,
            string message = "", NotificationAction? action = null) => host.Conditions[key] = title;

        public void ClearCondition(string key) => host.Conditions.Remove(key);
    }
}
