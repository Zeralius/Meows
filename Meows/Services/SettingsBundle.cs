using System.IO.Compression;
using System.Text.Json;

namespace Meows.Services;

/// <summary>What an import did, in numbers, and the one thing to say afterwards.</summary>
public sealed record BundleReport(int Files, int Skipped, string? BackupPath, string? Error)
{
    public bool Ok => Error is null;
}

/// <summary>
/// Everything under %APPDATA%\Meows as one zip, and back again: preferences, which plugins are
/// on, the bot folder, the history, every plugin's settings and data. Not the secrets, which are
/// sealed to this machine and this account and would be noise anywhere else; not the log, which
/// is this run's. Moving to a new machine used to be "copy the folder and hope".
/// </summary>
public static class SettingsBundle
{
    public const string Manifest = "meows-bundle.json";

    private static readonly string[] LeftOut = ["meows.log", ".bak", ".tmp"];

    /// <summary>Writes the bundle. The zip's paths are relative to the root, forward slashes.</summary>
    public static BundleReport Export(string root, string zipPath, string appVersion)
    {
        try
        {
            if (File.Exists(zipPath))
                File.Delete(zipPath);

            var files = 0;
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                    if (!Belongs(relative))
                        continue;
                    zip.CreateEntryFromFile(file, relative, CompressionLevel.Optimal);
                    files++;
                }

                var manifest = zip.CreateEntry(Manifest);
                using var stream = manifest.Open();
                JsonSerializer.Serialize(stream, new
                {
                    app = "Meows",
                    version = appVersion,
                    exported = DateTime.Now.ToString("O"),
                    machine = Environment.MachineName,
                    files,
                }, new JsonSerializerOptions { WriteIndented = true });
            }

            return new BundleReport(files, 0, null, null);
        }
        catch (Exception ex)
        {
            return new BundleReport(0, 0, null, ex.Message);
        }
    }

    /// <summary>
    /// Reads a bundle in over what is there. What is there first goes into a zip of its own
    /// beside the settings, so an import can be undone by importing that. Secrets in the bundle
    /// are never written, wherever the bundle came from, and neither is anything whose path
    /// would land outside the root.
    /// </summary>
    public static BundleReport Import(string root, string zipPath, string appVersion)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            if (zip.GetEntry(Manifest) is null)
                return new BundleReport(0, 0, null, "not a Meows settings bundle: no manifest inside");

            var backup = Path.Combine(root, $"before-import-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            var saved = Export(root, backup, appVersion);
            if (!saved.Ok)
                return new BundleReport(0, 0, null, $"could not keep a copy of the current settings first: {saved.Error}");

            var files = 0;
            var skipped = 0;
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            foreach (var entry in zip.Entries)
            {
                if (entry.FullName == Manifest || entry.FullName.EndsWith('/') || entry.Name.Length == 0)
                    continue;
                if (!Belongs(entry.FullName))
                {
                    skipped++;
                    continue;
                }

                var target = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                files++;
            }

            return new BundleReport(files, skipped, backup, null);
        }
        catch (Exception ex)
        {
            return new BundleReport(0, 0, null, ex.Message);
        }
    }

    /// <summary>What travels: everything but secrets, logs and leftovers, and never an earlier bundle.</summary>
    public static bool Belongs(string relative)
    {
        var parts = relative.Split('/', '\\');
        if (parts.Any(p => string.Equals(p, "secrets", StringComparison.OrdinalIgnoreCase)))
            return false;
        var name = parts[^1];
        if (name.StartsWith("before-import-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return false;
        return !LeftOut.Any(l => name.EndsWith(l, StringComparison.OrdinalIgnoreCase));
    }
}
