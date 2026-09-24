namespace Meows.Plugins.Abstractions;

/// <summary>What the shell hands a plugin. Everything here is scoped to that one plugin.</summary>
public interface IMeowsHost
{
    string PluginId { get; }

    /// <summary>Your own writable folder. Already created by the time you get it.</summary>
    string DataDirectory { get; }

    /// <summary>Goes to the shared log pane and meows.log. Safe from any thread.</summary>
    void Log(string message);

    /// <summary>
    /// The same, with a level, so a warning reads as one on the Log tab and survives a plugin
    /// being turned down to warnings only. Since 0.8.0; the default drops the level, so a shell
    /// from before still gets the line.
    /// </summary>
    void Log(LogLevel level, string message) => Log(message);

    /// <summary>The shell's notification surface, scoped to this plugin.</summary>
    IMeowsNotifications Notifications { get; }

    /// <summary>
    /// For work that should survive a tab switch. Cancelled for you on deactivation.
    /// </summary>
    IMeowsBackgroundWork Background { get; }

    /// <summary>Null if never saved, and also if the file is unreadable. Use ?? new().</summary>
    T? LoadSettings<T>() where T : class;

    void SaveSettings<T>(T settings) where T : class;

    /// <summary>
    /// The language the window is in. Ship a <c>Strings.&lt;code&gt;.json</c> per language as an
    /// embedded resource and the shell merges it in when your plugin is switched on.
    ///
    /// Has a default so a plugin written against 0.2.x keeps compiling. That default hands back
    /// the key it was given, which is what a plugin with no catalogue would get anyway.
    /// </summary>
    IMeowsText Text => MeowsText.Current;

    /// <summary>
    /// Sealed storage for credentials, scoped to this plugin. Never put those in settings.
    ///
    /// Has a default so a plugin written against 0.4.x keeps compiling; the default holds
    /// nothing and refuses to hold anything, which is what a plugin that never asked would get.
    /// </summary>
    IMeowsSecrets Secrets => NoSecrets.Instance;

    /// <summary>Hands work to another plugin. The default reaches nobody.</summary>
    IMeowsHandoff Handoff => NoHandoff.Instance;

    /// <summary>
    /// The shared store: a journal of what this plugin did, a notebook of small facts, and the
    /// one table every plugin shares, the hashes anything has seen. The default keeps nothing.
    /// </summary>
    IMeowsStore Store => NoStore.Instance;

    /// <summary>
    /// Every schedule every plugin is running, read-only. For the one plugin that wants to say
    /// what Meows is watching. The default watches nothing.
    /// </summary>
    IMeowsWatches Watches => NoWatches.Instance;

    /// <summary>
    /// The shell's file dialogs, so a view model can ask for a file or a folder without a
    /// TopLevel in hand. The default picks nothing, which is also what a shell with no window
    /// showing answers. Since 1.0.0.
    /// </summary>
    IMeowsPicker Pick => NoPicker.Instance;

    /// <summary>
    /// The server set on the Settings tab, to copy a folder to. The default reaches nothing,
    /// which is also what a shell answers when nobody has set one. Since 1.3.0.
    /// </summary>
    IMeowsReach Reach => NoReach.Instance;
}

/// <summary>What a shell built against an older contract answers. Nothing is kept.</summary>
public sealed class NoSecrets : IMeowsSecrets
{
    public static NoSecrets Instance { get; } = new();

    public bool Has(string name) => false;

    public string? Get(string name) => null;

    public void Set(string name, string value) =>
        throw new NotSupportedException("This shell does not keep secrets. Update Meows.");

    public void Forget(string name)
    {
    }
}

/// <summary>What a shell built against an older contract answers. Nobody is reachable.</summary>
public sealed class NoHandoff : IMeowsHandoff
{
    public static NoHandoff Instance { get; } = new();

    public bool CanReach(string pluginId) => false;

    public bool Send(string pluginId, Handoff handoff) => false;
}
