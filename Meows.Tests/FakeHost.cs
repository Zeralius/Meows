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

    /// <summary>The buttons on the last condition or post per key, so a test can press one.</summary>
    public Dictionary<string, IReadOnlyList<NotificationAction>> Buttons { get; } = [];

    public IMeowsNotifications Notifications => new FakeNotifications(this);

    public IMeowsBackgroundWork Background { get; } = new FakeBackgroundWork();

    /// <summary>What the shell would say every plugin is watching. A test fills it in and raises Changed.</summary>
    public FakeWatches Watches { get; } = new();

    IMeowsWatches IMeowsHost.Watches => Watches;

    public sealed class FakeWatches : IMeowsWatches
    {
        public List<WatchInfo> Items { get; } = [];

        public IReadOnlyList<WatchInfo> All() => Items.ToList();

        public event Action? Changed;

        public void Raise() => Changed?.Invoke();

        /// <summary>What a plugin asked to pause or resume, by id, in order.</summary>
        public List<(string Id, DateTime? Until)> Asked { get; } = [];

        public bool Pause(string id, DateTime until)
        {
            var i = Items.FindIndex(w => w.Id == id);
            if (i < 0)
                return false;
            Asked.Add((id, until));
            Items[i] = Items[i] with { PausedUntil = until };
            Raise();
            return true;
        }

        public bool Resume(string id)
        {
            var i = Items.FindIndex(w => w.Id == id);
            if (i < 0)
                return false;
            Asked.Add((id, null));
            Items[i] = Items[i] with { PausedUntil = null };
            Raise();
            return true;
        }
    }

    /// <summary>In memory, so a test never touches the Windows data protection API.</summary>
    public IMeowsSecrets Secrets { get; } = new FakeSecrets();

    /// <summary>Records what a plugin tried to hand to whom. Reaches whoever the test says.</summary>
    public FakeHandoff Handoffs { get; } = new();

    IMeowsHandoff IMeowsHost.Handoff => Handoffs;

    /// <summary>Everything a plugin recorded, in memory, in order.</summary>
    public FakeStore Store { get; } = new();

    IMeowsStore IMeowsHost.Store => Store;

    public sealed class FakeStore : IMeowsStore
    {
        private readonly Dictionary<string, string> _facts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SeenRecord> _seen = new(StringComparer.Ordinal);
        private long _next = 1;

        public List<StoredEvent> Events { get; } = [];

        public void Record(string kind, string subject, string? detail = null, IReadOnlyDictionary<string, string>? data = null) =>
            Events.Add(new StoredEvent(_next++, DateTime.Now, "meows.test", kind, subject, detail,
                data ?? new Dictionary<string, string>()));

        public IReadOnlyList<StoredEvent> Recent(int limit = 100, string? kind = null) =>
            Events.Where(e => kind is null || e.Kind == kind).Reverse().Take(limit).ToList();

        public IReadOnlyList<StoredEvent> Search(string text, int limit = 100) =>
            Events.Where(e => e.Subject.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                              (e.Detail?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false))
                .Reverse().Take(limit).ToList();

        public string? Get(string key) => _facts.GetValueOrDefault(key);

        public void Set(string key, string value) => _facts[key] = value;

        public void Remove(string key) => _facts.Remove(key);

        public void MarkSeen(string hash, string? note = null) =>
            _seen.TryAdd(hash, new SeenRecord(hash, "meows.test", DateTime.Now, note));

        public SeenRecord? Seen(string hash) => _seen.GetValueOrDefault(hash);
    }

    public sealed class FakeSecrets : IMeowsSecrets
    {
        private readonly Dictionary<string, string> _held = new(StringComparer.Ordinal);

        public bool Has(string name) => _held.ContainsKey(name);

        public string? Get(string name) => _held.GetValueOrDefault(name);

        public void Set(string name, string value) => _held[name] = value;

        public void Forget(string name) => _held.Remove(name);
    }

    public sealed class FakeHandoff : IMeowsHandoff
    {
        public HashSet<string> Reachable { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<(string To, Handoff What)> Sent { get; } = [];

        public bool CanReach(string pluginId) => Reachable.Contains(pluginId);

        public bool Send(string pluginId, Handoff handoff)
        {
            if (!Reachable.Contains(pluginId))
                return false;
            Sent.Add((pluginId, handoff));
            return true;
        }
    }

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
            NotificationAction? action = null) => Post(severity, title, message, action is null ? [] : [action]);

        public void Post(NotificationSeverity severity, string title, string message, params NotificationAction[] actions)
        {
            host.Lines.Add($"post: {title}");
            host.Buttons["post:" + title] = actions;
        }

        public void SetCondition(string key, NotificationSeverity severity, string title,
            string message = "", NotificationAction? action = null) => SetCondition(key, severity, title, message, action is null ? [] : [action]);

        public void SetCondition(string key, NotificationSeverity severity, string title, string message, params NotificationAction[] actions)
        {
            host.Conditions[key] = title;
            host.Buttons[key] = actions;
        }

        public void ClearCondition(string key) => host.Conditions.Remove(key);
    }
}
