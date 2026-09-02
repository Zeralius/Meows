using System.Text.Json;

namespace Meows.Services;

/// <summary>Everything we persist, under %APPDATA%\Meows. Nothing is written into the repo.</summary>
public sealed class ShellSettings
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// The root is injectable so tests can use a throwaway directory. The app always uses the
    /// default.
    /// </summary>
    public ShellSettings(string? root = null, string? previousRoot = null)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Root = root ?? Path.Combine(appData, "Meows");

        // Only look at the real old folder on a real run. A test with its own Root has to pass
        // its own previous folder too, or it would read the actual settings on this machine.
        var previous = previousRoot ?? (root is null ? Path.Combine(appData, "Mews") : null);

        // Whether there are settings here, not whether the folder exists. CrashLog runs first
        // and creates the folder to write into, so an empty folder is normal on a first run.
        // Treating that as "already set up" skipped the migration entirely.
        if (previous is not null && !AlreadySettled(Root))
            CarryOverFromMews(previous, Root);

        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>
    /// The activation list is stored as plugin ids, and the ids contain the app name. Copying
    /// the file across unchanged would leave every plugin switched off.
    /// </summary>
    private static void RenameIdsIn(string file)
    {
        try
        {
            if (!File.Exists(file))
                return;

            var text = File.ReadAllText(file);
            var renamed = text.Replace("\"mews.", "\"meows.");

            if (renamed != text)
                File.WriteAllText(file, renamed);
        }
        catch (Exception)
        {
            // Worst case they re-tick their plugins. Not worth failing the migration over.
        }
    }

    /// <summary>Whether this folder holds settings, as opposed to just existing.</summary>
    private static bool AlreadySettled(string root) =>
        File.Exists(Path.Combine(root, "activated-plugins.json")) ||
        Directory.Exists(Path.Combine(root, "plugins"));

    /// <summary>
    /// What happened during the migration, if anything. Set in the constructor and read once
    /// the log exists, which is later.
    /// </summary>
    public string? StartupNote { get; private set; }

    /// <summary>
    /// The app was called Mews until 1.0.0 and its settings lived under that name. Brings the
    /// old folder across on first run, rather than starting empty and looking like everything
    /// was lost.
    ///
    /// Copies rather than moves, so the original is still there if this goes wrong.
    /// </summary>
    private void CarryOverFromMews(string old, string wanted)
    {
        try
        {
            if (!Directory.Exists(old))
                return;

            // Created up front: an old folder with files but no subfolders makes nothing in the
            // loop below, and every copy then fails for want of a destination.
            Directory.CreateDirectory(wanted);

            foreach (var directory in Directory.GetDirectories(old, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(directory.Replace(old, wanted));

            foreach (var file in Directory.GetFiles(old, "*", SearchOption.AllDirectories))
            {
                // The old log belongs to the old name and is not settings. Copying it would
                // leave two logs, neither of them complete.
                if (Path.GetExtension(file).Equals(".log", StringComparison.OrdinalIgnoreCase))
                    continue;

                File.Copy(file, file.Replace(old, wanted), overwrite: false);
            }

            // Plugin ids changed with the app name, and each folder is named after its id.
            var plugins = Path.Combine(wanted, "plugins");
            if (Directory.Exists(plugins))
            {
                foreach (var directory in Directory.GetDirectories(plugins, "mews.*"))
                {
                    var renamed = Path.Combine(plugins, "meows." + Path.GetFileName(directory)["mews.".Length..]);
                    if (!Directory.Exists(renamed))
                        Directory.Move(directory, renamed);
                }
            }

            RenameIdsIn(Path.Combine(wanted, "activated-plugins.json"));

            StartupNote = $"Settings were carried over from {old}, which is where they lived when " +
                          "this was called Mews. The old folder has been left exactly as it was.";
        }
        catch (Exception ex)
        {
            StartupNote = $"Could not carry settings over from {old}: {ex.Message}. " +
                          "Nothing was changed there, so it can be copied across by hand.";
        }
    }

    /// <summary>
    /// Where to report a file that could not be read. Wired up to the shell log after this is
    /// constructed, hence a property rather than a constructor argument.
    /// </summary>
    public Action<string>? Report { get; set; }

    private string ActivationFile => Path.Combine(Root, "activated-plugins.json");

    public string PluginDataDirectory(string pluginId)
    {
        var safe = string.Concat(pluginId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var dir = Path.Combine(Root, "plugins", safe);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public HashSet<string> LoadActivatedPlugins()
    {
        try
        {
            if (!File.Exists(ActivationFile))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ids = JsonSerializer.Deserialize<string[]>(File.ReadAllText(ActivationFile), Json);
            return new HashSet<string>(ids ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            SetAside(ActivationFile, ex);
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void SaveActivatedPlugins(IEnumerable<string> ids)
    {
        try
        {
            WriteWholly(ActivationFile, JsonSerializer.Serialize(ids.ToArray(), Json));
        }
        catch (Exception ex)
        {
            // Worst case they re-tick a plugin. Not worth a dialog, but worth logging.
            Report?.Invoke($"Could not save which plugins are active: {ex.Message}");
        }
    }

    public T? LoadPluginSettings<T>(string pluginId) where T : class
    {
        var file = Path.Combine(PluginDataDirectory(pluginId), "settings.json");
        if (!File.Exists(file))
            return null;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(file), Json);
        }
        catch (Exception ex)
        {
            SetAside(file, ex);
            return null;
        }
    }

    public void SavePluginSettings<T>(string pluginId, T settings) where T : class
    {
        var file = Path.Combine(PluginDataDirectory(pluginId), "settings.json");
        WriteWholly(file, JsonSerializer.Serialize(settings, Json));
    }

    /// <summary>
    /// Renames a file we could not read, and logs it.
    ///
    /// Returning null on a parse failure meant the plugin started on defaults, and the next
    /// setting anyone changed wrote those defaults over the file. One bad byte lost everything,
    /// silently. Renaming first keeps the original around to look at.
    /// </summary>
    private void SetAside(string file, Exception ex)
    {
        var moved = $"{file}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";

        try
        {
            File.Move(file, moved);
            Report?.Invoke($"{Path.GetFileName(file)} could not be read ({ex.Message}). " +
                           $"It has been kept as {Path.GetFileName(moved)} and defaults are being used.");
        }
        catch (Exception moveFailed)
        {
            // Could not even rename it, so leave it alone. Overwriting an unreadable file is
            // what this method exists to prevent.
            Report?.Invoke($"{Path.GetFileName(file)} could not be read ({ex.Message}) " +
                           $"and could not be set aside ({moveFailed.Message}).");
        }
    }

    /// <summary>
    /// Writes via a temporary file so the real one holds either the old contents or the new
    /// ones, never half of each. A write interrupted by a crash is how you get the unreadable
    /// file the method above has to deal with.
    /// </summary>
    private static void WriteWholly(string file, string contents)
    {
        var temporary = file + ".writing";
        File.WriteAllText(temporary, contents);

        if (File.Exists(file))
            File.Replace(temporary, file, destinationBackupFileName: null);
        else
            File.Move(temporary, file);
    }
}
