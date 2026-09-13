using Meows.Plugins.Abstractions;

namespace Meows.Services;

/// <summary>One of these per activated plugin. This is the IMeowsHost they see.</summary>
public sealed class PluginHost : IMeowsHost
{
    private readonly ShellSettings _settings;
    private readonly ShellLog _log;

    public PluginHost(
        string pluginId,
        string displayName,
        ShellSettings settings,
        ShellLog log,
        NotificationCenter notifications,
        BackgroundTaskService background,
        IMeowsHandoff? handoff = null,
        IMeowsStore? store = null)
    {
        PluginId = pluginId;
        _settings = settings;
        _log = log;
        DisplayName = displayName;
        DataDirectory = settings.PluginDataDirectory(pluginId);
        Notifications = new PluginNotifications(notifications, displayName);
        Background = new PluginBackgroundWork(background, pluginId, displayName);
        Secrets = new SecretStore(DataDirectory);
        Handoff = handoff ?? NoHandoff.Instance;
        Store = store ?? NoStore.Instance;
        Watches = new WatchService(background);
    }

    public IMeowsWatches Watches { get; }

    public IMeowsSecrets Secrets { get; }

    public IMeowsHandoff Handoff { get; }

    public IMeowsStore Store { get; }

    public string PluginId { get; }

    public string DisplayName { get; }

    public string DataDirectory { get; }

    public IMeowsNotifications Notifications { get; }

    public IMeowsBackgroundWork Background { get; }

    public IMeowsText Text => MeowsText.Current;

    public void Log(string message) => _log.Write(DisplayName, message);

    public T? LoadSettings<T>() where T : class => _settings.LoadPluginSettings<T>(PluginId);

    public void SaveSettings<T>(T settings) where T : class => _settings.SavePluginSettings(PluginId, settings);
}
