using System.Text.Json;

namespace Meows.Services;

/// <summary>Everything we persist, all under %APPDATA%\Meows. Nothing goes in the repo.</summary>
public sealed class ShellSettings
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// The root is injectable so the tests can work somewhere disposable. Left alone it is the
    /// real one, which is the only thing the app ever passes.
    /// </summary>
    public ShellSettings(string? root = null, string? previousRoot = null)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Root = root ?? Path.Combine(appData, "Meows");

        // The real previous folder only when this is the real run. A test that points Root
        // somewhere disposable must say where its own previous folder is, or it would reach into
        // the actual settings on the machine running it.
        var previous = previousRoot ?? (root is null ? Path.Combine(appData, "Mews") : null);

        // Whether anything is settled here, not whether the folder exists. The crash log is armed
        // before any of this and creates the folder to write into, so an empty folder is the
        // normal state on a first run and treating it as "already set up" skipped the move
        // entirely.
        if (previous is not null && !AlreadySettled(Root))
            CarryOverFromMews(previous, Root);

        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>
    /// The list of switched on plugins is written in terms of their ids, and the ids carry the
    /// app's name. Copying the file across without this leaves every plugin switched off and
    /// looking like the move lost them.
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
            // Worst case they re-tick the plugins they had on. Not worth failing the move over.
        }
    }

    /// <summary>Whether this folder already holds settings, as opposed to merely existing.</summary>
    private static bool AlreadySettled(string root) =>
        File.Exists(Path.Combine(root, "activated-plugins.json")) ||
        Directory.Exists(Path.Combine(root, "plugins"));

    /// <summary>
    /// What happened during the move from the old name, if anything. Set during construction and
    /// read once the log exists, since that is built after this.
    /// </summary>
    public string? StartupNote { get; private set; }

    /// <summary>
    /// The app was called Mews until 1.0.0, and its settings lived under that name. Rather than
    /// silently starting empty and looking like everything was forgotten, the old folder is
    /// brought across the first time.
    ///
    /// Copied rather than moved. If any of this goes wrong the original is still sitting there to
    /// go back to, which matters more than tidiness for something that runs once.
    /// </summary>
    private void CarryOverFromMews(string old, string wanted)
    {
        try
        {
            if (!Directory.Exists(old))
                return;

            // Before anything is copied into it. An old folder holding files but no subfolders
            // creates nothing in the loop below, and every copy then fails for want of somewhere
            // to land.
            Directory.CreateDirectory(wanted);

            foreach (var directory in Directory.GetDirectories(old, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(directory.Replace(old, wanted));

            foreach (var file in Directory.GetFiles(old, "*", SearchOption.AllDirectories))
            {
                // The old log is history under the old name, not settings. Carrying it across
                // would leave two logs side by side and neither of them the whole story.
                if (Path.GetExtension(file).Equals(".log", StringComparison.OrdinalIgnoreCase))
                    continue;

                File.Copy(file, file.Replace(old, wanted), overwrite: false);
            }

            // The plugin ids moved with the name, and each one's folder is named after its id.
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
    /// Where to say that something on disk could not be read. Wired to the shell log once it
    /// exists, which is after this, so it is a property rather than a constructor argument.
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
            // Worst case they re-tick a plugin. Not worth an error dialog, but not worth
            // saying nothing about either.
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
    /// Moves a file that could not be read out of the way, and says so.
    ///
    /// Returning null on a parse failure meant the plugin started on defaults and the next
    /// setting anyone changed wrote those defaults straight over the file. One unreadable byte
    /// therefore lost the lot, silently. Renaming it first means the original is still there to
    /// look at, and the log says where it went.
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
            // Could not even rename it. Say so and leave it exactly where it is: overwriting
            // something unreadable is what this whole method exists to prevent.
            Report?.Invoke($"{Path.GetFileName(file)} could not be read ({ex.Message}) " +
                           $"and could not be set aside ({moveFailed.Message}).");
        }
    }

    /// <summary>
    /// Writes through a temporary file so the real one is either the old contents or the new
    /// ones, never half of each. A settings file truncated by a crash mid write is precisely how
    /// you end up with the unreadable file above.
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
