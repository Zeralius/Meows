using Mews.Plugins.Abstractions;

namespace Mews.Services;

/// <summary>One of these per activated plugin. This is the IMewsHost they see.</summary>
public sealed class PluginHost : IMewsHost
{
    private readonly ShellSettings _settings;
    private readonly ShellLog _log;

    public PluginHost(
        string pluginId,
        string displayName,
        ShellSettings settings,
        ShellLog log,
        NotificationCenter notifications,
        BackgroundTaskService background)
    {
        PluginId = pluginId;
        _settings = settings;
        _log = log;
        DisplayName = displayName;
        DataDirectory = settings.PluginDataDirectory(pluginId);
        Notifications = new PluginNotifications(notifications, displayName);
        Background = new PluginBackgroundWork(background, pluginId, displayName);
    }

    public string PluginId { get; }

    public string DisplayName { get; }

    public string DataDirectory { get; }

    public IMewsNotifications Notifications { get; }

    public IMewsBackgroundWork Background { get; }

    public void Log(string message) => _log.Write(DisplayName, message);

    public T? LoadSettings<T>() where T : class => _settings.LoadPluginSettings<T>(PluginId);

    public void SaveSettings<T>(T settings) where T : class => _settings.SavePluginSettings(PluginId, settings);
}
