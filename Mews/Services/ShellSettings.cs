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

    public ShellSettings()
    {
        Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Mews");
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

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
        catch (Exception)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void SaveActivatedPlugins(IEnumerable<string> ids)
    {
        try
        {
            File.WriteAllText(ActivationFile, JsonSerializer.Serialize(ids.ToArray(), Json));
        }
        catch (Exception)
        {
            // Worst case they re-tick a plugin. Not worth an error dialog.
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
        catch (Exception)
        {
            return null;
        }
    }

    public void SavePluginSettings<T>(string pluginId, T settings) where T : class
    {
        var file = Path.Combine(PluginDataDirectory(pluginId), "settings.json");
        File.WriteAllText(file, JsonSerializer.Serialize(settings, Json));
    }
}
