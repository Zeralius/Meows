using System.Text.Json;
using Meows.Plugins.Abstractions;

namespace Meows.Tests;

/// <summary>
/// Enough of the shell to construct a plugin view model in a test. Settings round-trip through
/// JSON rather than being handed back as the same object, so a view model that mutates what it
/// was given cannot pass by accident.
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
    /// Accepts work and records it, without running it. A view model that kicks off a scan in
    /// its constructor should not drag a real filesystem walk into every test that happens to
    /// build one, and the tests that care about results call the view model's own results entry
    /// point instead. What is worth asserting here is that the work was asked for.
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
