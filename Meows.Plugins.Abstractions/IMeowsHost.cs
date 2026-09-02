namespace Meows.Plugins.Abstractions;

/// <summary>What the shell hands a plugin. Everything here is scoped to that one plugin.</summary>
public interface IMeowsHost
{
    string PluginId { get; }

    /// <summary>Your own writable folder. Already created by the time you get it.</summary>
    string DataDirectory { get; }

    /// <summary>Goes to the shared log pane and meows.log. Safe from any thread.</summary>
    void Log(string message);

    /// <summary>The shell's notification surface, scoped to this plugin.</summary>
    IMeowsNotifications Notifications { get; }

    /// <summary>
    /// For work that should survive a tab switch. Cancelled for you on deactivation.
    /// </summary>
    IMeowsBackgroundWork Background { get; }

    /// <summary>Null if never saved, and also if the file is unreadable. Use ?? new().</summary>
    T? LoadSettings<T>() where T : class;

    void SaveSettings<T>(T settings) where T : class;
}
