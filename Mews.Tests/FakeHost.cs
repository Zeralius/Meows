using System.Text.Json;
using Mews.Plugins.Abstractions;

namespace Mews.Tests;

/// <summary>
/// Enough of the shell to construct a plugin view model in a test. Settings round-trip through
/// JSON rather than being handed back as the same object, so a view model that mutates what it
/// was given cannot pass by accident.
/// </summary>
public sealed class FakeHost : IMewsHost
{
    private string? _settings;

    public FakeHost(string dataDirectory)
    {
        DataDirectory = dataDirectory;
        Directory.CreateDirectory(dataDirectory);
    }

    public string PluginId => "mews.test";

    public string DataDirectory { get; }

    public List<string> Lines { get; } = [];

    public Dictionary<string, string> Conditions { get; } = [];

    public IMewsNotifications Notifications => new FakeNotifications(this);

    public IMewsBackgroundWork Background =>
        throw new NotSupportedException("No test needs background work yet.");

    public void Log(string message) => Lines.Add(message);

    public T? LoadSettings<T>() where T : class =>
        _settings is null ? null : JsonSerializer.Deserialize<T>(_settings);

    public void SaveSettings<T>(T settings) where T : class =>
        _settings = JsonSerializer.Serialize(settings);

    private sealed class FakeNotifications(FakeHost host) : IMewsNotifications
    {
        public void Post(NotificationSeverity severity, string title, string message = "",
            NotificationAction? action = null) => host.Lines.Add($"post: {title}");

        public void SetCondition(string key, NotificationSeverity severity, string title,
            string message = "", NotificationAction? action = null) => host.Conditions[key] = title;

        public void ClearCondition(string key) => host.Conditions.Remove(key);
    }
}
