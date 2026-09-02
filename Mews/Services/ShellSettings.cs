using System.Text.Json;

namespace Mews.Services;

/// <summary>Everything we persist, all under %APPDATA%\Mews. Nothing goes in the repo.</summary>
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
    public ShellSettings(string? root = null)
    {
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Mews");
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

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
