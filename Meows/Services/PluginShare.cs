using System.IO.Compression;
using System.Text.Json;

namespace Meows.Services;

/// <summary>What a shared-settings zip says on the outside.</summary>
public sealed record SharedManifest(string PluginId, string Name, string AppVersion, DateTime ExportedUtc);

/// <summary>
/// Sharing one plugin's settings with someone: a zip with a manifest and the settings file as
/// it stands, and back again with the current file kept aside. Raw files in, raw files out —
/// the shell never learns what any plugin keeps, which is exactly why one mechanism fits all
/// of them. Secrets never travel: they live sealed beside the settings, not in them.
/// </summary>
public static class PluginShare
{
    public const string ManifestName = "manifest.json";

    public const string SettingsName = "settings.json";

    /// <summary>The file the current settings are kept aside as on import, beside themselves.</summary>
    public const string BackupName = "settings.shared-backup.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>
    /// Where one plugin's settings live, when it keeps any. Null when the plugin keeps nothing,
    /// which is not an error but means there is nothing to share.
    /// </summary>
    public static string? SettingsFile(string settingsRoot, string pluginId)
    {
        try
        {
            var safe = string.Concat(pluginId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var file = Path.Combine(settingsRoot, "plugins", safe, "settings.json");
            return File.Exists(file) ? file : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the zip: the manifest naming the plugin, and its settings file as it stands.
    /// Throws when the plugin keeps nothing, with the reason a person can act on.
    /// </summary>
    public static void Export(string settingsRoot, string pluginId, string name, string appVersion, string zipPath)
    {
        var settings = SettingsFile(settingsRoot, pluginId)
            ?? throw new InvalidOperationException($"'{name}' keeps no settings yet, so there is nothing to share.");

        var manifest = new SharedManifest(pluginId, name, appVersion, DateTime.UtcNow);
        var directory = Path.GetDirectoryName(Path.GetFullPath(zipPath));
        if (directory is not null)
            Directory.CreateDirectory(directory);

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var manifestEntry = zip.CreateEntry(ManifestName);
        using (var writer = new StreamWriter(manifestEntry.Open()))
            writer.Write(JsonSerializer.Serialize(manifest, Json));
        zip.CreateEntryFromFile(settings, SettingsName, CompressionLevel.Optimal);
    }

    /// <summary>What an import found: whose settings, and whether anything was kept aside.</summary>
    public sealed record SharedImport(string PluginId, string Name, bool BackedUp);

    /// <summary>
    /// Reads a shared zip back in: validates the manifest, keeps the current settings aside
    /// when there are any, and puts the shared ones where the plugin reads them. Throws with
    /// the reason when the zip is not a shared one.
    /// </summary>
    public static SharedImport Import(string settingsRoot, string zipPath)
    {
        SharedManifest manifest;
        string settings;
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var manifestEntry = zip.GetEntry(ManifestName)
                ?? throw new InvalidOperationException("That zip has no manifest, so it is not shared settings.");
            using (var reader = new StreamReader(manifestEntry.Open()))
                manifest = JsonSerializer.Deserialize<SharedManifest>(reader.ReadToEnd(), Json)
                    ?? throw new InvalidOperationException("That zip has no manifest, so it is not shared settings.");
            var settingsEntry = zip.GetEntry(SettingsName)
                ?? throw new InvalidOperationException("That zip names a plugin but holds no settings.");
            settings = new StreamReader(settingsEntry.Open()).ReadToEnd();
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"That zip could not be read: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(manifest.PluginId))
            throw new InvalidOperationException("That zip names no plugin, so there is nowhere to put it.");

        // Validated JSON in, validated JSON out: shared settings that do not parse are refused
        // rather than landing as a file the plugin cannot read.
        try
        {
            using var _ = JsonDocument.Parse(settings);
        }
        catch (Exception)
        {
            throw new InvalidOperationException("The settings in that zip do not parse, so they were left alone.");
        }

        var safe = string.Concat(manifest.PluginId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var directory = Path.Combine(settingsRoot, "plugins", safe);
        Directory.CreateDirectory(directory);

        var backedUp = false;
        var current = Path.Combine(directory, SettingsName);
        if (File.Exists(current))
        {
            File.Copy(current, Path.Combine(directory, BackupName), overwrite: true);
            backedUp = true;
        }
        File.WriteAllText(current, settings);

        return new SharedImport(manifest.PluginId, manifest.Name, backedUp);
    }
}
