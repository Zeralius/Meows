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
        public List<string> Requested { get; } = [];

        public IBackgroundTask Run(string title, Func<IBackgroundContext, Task> work)
        {
            Requested.Add(title);
            return new DeadTask(title);
        }

        public IBackgroundTask Schedule(string title, TimeSpan interval, Func<IBackgroundContext, Task> work,
            bool runImmediately = true)
        {
            Requested.Add(title);
            return new DeadTask(title);
        }

        private sealed class DeadTask(string title) : IBackgroundTask
        {
            public string Title { get; } = title;

            public bool IsRunning => false;

            public void Cancel()
            {
            }

            public void Dispose()
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
