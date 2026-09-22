using System.Text.Json;

namespace Meows.Bot;

/// <summary>
/// Where the posting bot is, remembered once for every plugin that talks to it.
///
/// Four plugins asked this question, each with its own setting, its own button and its own
/// error string, and picking the folder in one told the other three nothing. Now it is one file
/// under Meows' own settings folder: pick the bot in any tab and every tab that was never told
/// otherwise reads the same answer. A root a plugin was given by hand before this existed is
/// still honoured, so nothing already set up changes under anyone.
/// </summary>
public static class BotLocation
{
    /// <summary>
    /// Overridable so a test can keep its answers to itself. The shell says where its settings
    /// are through MEOWS_SETTINGS_ROOT, which is the roaming profile or, for a portable Meows,
    /// a folder beside the exe; the profile is only the fallback for running without a shell.
    /// </summary>
    public static string SettingsFolder { get; set; } =
        Environment.GetEnvironmentVariable("MEOWS_SETTINGS_ROOT") is { Length: > 0 } root
            ? root
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Meows");

    private static string FilePath => Path.Combine(SettingsFolder, "bot.json");

    private sealed class Stored
    {
        public string? Root { get; set; }
    }

    /// <summary>The shared answer, or null when nobody has picked one yet.</summary>
    public static string? Shared()
    {
        try
        {
            if (!File.Exists(FilePath))
                return null;

            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath));
            return stored?.Root is { Length: > 0 } root && Directory.Exists(root) ? root : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Remembers the folder for every plugin. Called when one of them picks it.</summary>
    public static void Remember(string root)
    {
        try
        {
            Directory.CreateDirectory(SettingsFolder);
            var json = JsonSerializer.Serialize(new Stored { Root = Path.GetFullPath(root) },
                new JsonSerializerOptions { WriteIndented = true });
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception)
        {
            // The plugin still has its own copy, and the probe still finds a checkout beside the
            // exe. Losing the shared note is a nuisance, not a failure.
        }
    }

    /// <summary>
    /// The bot's folder. A root this plugin was given by hand still wins, so nothing that was set
    /// up before the shared answer existed changes under anyone; then the shared pick, which is
    /// what a plugin that was never told anything follows; then a checkout found above the exe.
    /// </summary>
    public static string? Resolve(string? pluginSaved)
    {
        if (!string.IsNullOrWhiteSpace(pluginSaved) && Directory.Exists(pluginSaved))
            return Path.GetFullPath(pluginSaved);

        return Shared() ?? BotWorkspace.Probe(null);
    }
}
