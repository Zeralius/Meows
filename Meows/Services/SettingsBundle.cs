using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Meows.Services;

/// <summary>What an import did, in numbers, and the one thing to say afterwards.</summary>
public sealed record BundleReport(int Files, int Skipped, string? BackupPath, string? Error)
{
    public bool Ok => Error is null;

    /// <summary>Rules that came in with the bundle and were switched off to be looked at first.</summary>
    public int RulesSwitchedOff { get; init; }
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

    /// <summary>A bundle waiting for the next start, beside the settings.</summary>
    public const string Pending = "pending-import.zip";

    // SQLite's journal and write-ahead files belong to one open database at one moment; carried
    // to another folder beside a different meows.db they are corruption waiting to be read. A
    // .writing file is a settings save that never finished.
    private static readonly string[] LeftOut = [".log", ".bak", ".tmp", "-journal", "-wal", "-shm", ".writing"];

    /// <summary>
    /// Checks a bundle and puts it beside the settings, to be read in at the next start. Read in
    /// while Meows runs, the running app wrote its own preferences, its list of what is on and
    /// each plugin's settings back over the import, and replaced the history database while it
    /// was open; staged, nothing has any of it open yet.
    /// </summary>
    public static BundleReport Stage(string root, string zipPath)
    {
        try
        {
            int files;
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                if (zip.GetEntry(Manifest) is null)
                    return new BundleReport(0, 0, null, "not a Meows settings bundle: no manifest inside");
                files = zip.Entries.Count(e => e.FullName != Manifest && e.Name.Length > 0 && Belongs(e.FullName));
            }

            Directory.CreateDirectory(root);
            File.Copy(zipPath, Path.Combine(root, Pending), overwrite: true);
            return new BundleReport(files, 0, null, null);
        }
        catch (Exception ex)
        {
            return new BundleReport(0, 0, null, ex.Message);
        }
    }

    /// <summary>
    /// At start, before anything opens a settings file: reads in a staged bundle, if there is one,
    /// and removes it. Null when nothing was waiting.
    ///
    /// Two things in a bundle are not taken on trust, since a bundle can come from somebody else.
    /// The server plugins copy to stays this machine's: an imported one would send files, with
    /// this machine's own key, wherever the bundle said, under a host key nobody here pressed
    /// Trust for. And a standing rule that was not here before arrives switched off, to be looked
    /// at on the Rules tab before it acts.
    /// </summary>
    public static BundleReport? ApplyPending(string root, string appVersion)
    {
        var pending = Path.Combine(root, Pending);
        if (!File.Exists(pending))
            return null;

        var preferencesFile = Path.Combine(root, "preferences.json");
        var before = ReadJson(preferencesFile);
        var report = Import(root, pending, appVersion);
        try
        {
            File.Delete(pending);
        }
        catch (Exception)
        {
            // Read in again next start, which writes the same files: harmless.
        }

        if (report.Ok && ReadJson(preferencesFile) is JsonObject after)
        {
            KeepOwnServer(after, before as JsonObject);
            var switchedOff = SwitchOffNewRules(after, before as JsonObject);
            File.WriteAllText(preferencesFile, after.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            report = report with { RulesSwitchedOff = switchedOff };
        }

        return report;
    }

    private static JsonNode? ReadJson(string file)
    {
        try
        {
            return File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void KeepOwnServer(JsonObject after, JsonObject? before)
    {
        after.Remove("reach");
        if (before?["reach"] is { } own)
            after["reach"] = own.DeepClone();
    }

    private static int SwitchOffNewRules(JsonObject after, JsonObject? before)
    {
        if (after["rules"] is not JsonArray rules)
            return 0;
        var known = (before?["rules"] as JsonArray)?.Select(r => r?.ToJsonString()).ToHashSet() ?? [];
        var count = 0;
        foreach (var rule in rules.OfType<JsonObject>())
        {
            if (known.Contains(rule.ToJsonString()))
                continue;
            if (rule["enabled"] is JsonValue on && on.TryGetValue<bool>(out var enabled) && !enabled)
                continue;
            rule["enabled"] = false;
            count++;
        }
        return count;
    }

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
        if (string.Equals(name, Pending, StringComparison.OrdinalIgnoreCase))
            return false;
        // A plugin's own word that a file is not for travelling: Tin's account numbers.
        if (name.EndsWith(".private.json", StringComparison.OrdinalIgnoreCase))
            return false;
        return !LeftOut.Any(l => name.EndsWith(l, StringComparison.OrdinalIgnoreCase));
    }
}
