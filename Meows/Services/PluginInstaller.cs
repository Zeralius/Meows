using System.IO.Compression;
using System.Text.Json;

namespace Meows.Services;

/// <summary>What an install did: the plugin's folder name, whether it had to wait, or why not.</summary>
public sealed record InstallReport(string? Name, bool Pending, string? Error)
{
    public bool Ok => Error is null;
}

/// <summary>
/// Left in a plugin's folder by the installer, and by nothing else, so a folder that has one was
/// put there through the Plugins tab and can be taken away through it. A built-in plugin and a
/// folder copied by hand have none, and the shell leaves those alone.
/// </summary>
public sealed record InstallRecord(string From, DateTime On, string? Source = null);

/// <summary>
/// A plugin's release is its build folder as a zip. This puts that folder where the shell scans,
/// so nobody has to know where that is. The zip may hold the folder itself or just its files:
/// either way the plugin lands as <c>plugins\Name\Name.dll</c>, which is the layout the catalog
/// prefers, named after the one DLL that is the plugin rather than after whatever the zip was
/// called.
///
/// A plugin that is already installed is already loaded, and its DLLs are held open until Meows
/// quits, so the new version cannot go over the old one. It goes beside it, as <c>Name.update</c>,
/// and the catalog swaps the two before its next scan, which at the latest is the next start.
/// </summary>
public static class PluginInstaller
{
    public const string PendingSuffix = ".update";
    public const string Marker = "meows-install.json";

    /// <summary>True while a newer version of this plugin is still waiting beside it.</summary>
    public static bool IsWaiting(string pluginsDirectory, string name) =>
        Directory.Exists(Path.Combine(pluginsDirectory, name + PendingSuffix));
    private const string StagingSuffix = ".installing";
    private const string RetiredSuffix = ".old";

    /// <summary>
    /// Folders the catalog must not scan: one being written, one waiting to be swapped in, one
    /// just swapped out and not yet gone.
    /// </summary>
    public static bool IsTransient(string folder) =>
        folder.EndsWith(PendingSuffix, StringComparison.OrdinalIgnoreCase) ||
        folder.EndsWith(StagingSuffix, StringComparison.OrdinalIgnoreCase) ||
        folder.EndsWith(RetiredSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The installer's note in this folder, or null when it was not the installer that made it.</summary>
    public static InstallRecord? RecordOf(string pluginFolder)
    {
        try
        {
            var path = Path.Combine(pluginFolder, Marker);
            return File.Exists(path) ? JsonSerializer.Deserialize<InstallRecord>(File.ReadAllText(path)) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Takes an installed plugin away. Renamed aside rather than deleted, for the same reason
    /// the update is: the folder is held open while its assembly is loaded, and a rename is all
    /// or nothing. The retired folder goes when it can, at the latest on the next start. The
    /// plugin's settings under %APPDATA% are not touched; they are the user's, not the plugin's.
    /// </summary>
    public static string? Uninstall(string pluginFolder)
    {
        try
        {
            var retired = pluginFolder.TrimEnd(Path.DirectorySeparatorChar) + RetiredSuffix;
            TryDelete(retired);
            Directory.Move(pluginFolder, retired);
            TryDelete(retired);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public static InstallReport Install(string zipPath, string pluginsDirectory, string? source = null)
    {
        string? staging = null;
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            // Forward slashes throughout: a zip made by Compress-Archive names its entries with
            // backslashes, and a folder inside it would otherwise go unnoticed and be nested.
            var files = zip.Entries
                .Select(e => (Entry: e, Path: e.FullName.Replace('\\', '/')))
                .Where(f => !f.Path.EndsWith('/'))
                .ToList();
            if (files.Count == 0)
                return new InstallReport(null, false, "the zip is empty");

            var prefix = CommonFolder(files.Select(f => f.Path).ToList());
            var name = PluginName(files.Select(f => f.Path).ToList(), prefix);
            if (name is null)
                return new InstallReport(null, false, "could not tell which DLL is the plugin; zip the build folder as it is");

            var target = Path.Combine(pluginsDirectory, name);
            staging = target + StagingSuffix;
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);

            var root = Path.GetFullPath(staging + Path.DirectorySeparatorChar);
            foreach (var (entry, path) in files)
            {
                var relative = path[prefix.Length..];
                var destination = Path.GetFullPath(Path.Combine(staging, relative));
                if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return new InstallReport(null, false, $"refused {path}: it would land outside the plugin's folder");

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }

            File.WriteAllText(Path.Combine(staging, Marker), JsonSerializer.Serialize(
                new InstallRecord(Path.GetFileName(zipPath), DateTime.Now, source),
                new JsonSerializerOptions { WriteIndented = true }));

            if (!Directory.Exists(target))
            {
                Directory.Move(staging, target);
                return new InstallReport(name, false, null);
            }

            // Already here, so already loaded and held open. Leave the new one waiting beside it;
            // the next scan swaps them if it can.
            var pending = target + PendingSuffix;
            if (Directory.Exists(pending))
                Directory.Delete(pending, recursive: true);
            Directory.Move(staging, pending);
            return new InstallReport(name, true, null);
        }
        catch (InvalidDataException)
        {
            return new InstallReport(null, false, "not a zip file");
        }
        catch (Exception ex)
        {
            return new InstallReport(null, false, ex.Message);
        }
        finally
        {
            try
            {
                if (staging is not null && Directory.Exists(staging))
                    Directory.Delete(staging, recursive: true);
            }
            catch (Exception)
            {
                // Best effort; the catalog skips it either way.
            }
        }
    }

    /// <summary>
    /// Swaps every waiting <c>Name.update</c> in for its <c>Name</c>. Called before a scan. The
    /// old folder is renamed aside rather than deleted, because deleting would take half of it
    /// before hitting a file that is held open, and a rename either happens whole or is refused
    /// whole. Windows lets a folder be renamed around a loaded assembly, so mid-run the swap
    /// usually goes through and the rescan that follows picks the new version up; the retired
    /// folder cannot be deleted until the old assembly is let go at exit, and is cleared next
    /// time. When the rename is refused, the old plugin stays whole and the new one keeps
    /// waiting; whether it did is read from whether <c>Name.update</c> is still there.
    /// </summary>
    public static IReadOnlyList<string> ApplyPending(string pluginsDirectory)
    {
        var said = new List<string>();
        List<string> waiting;
        try
        {
            waiting = Directory.EnumerateDirectories(pluginsDirectory, "*" + PendingSuffix).ToList();
            foreach (var retired in Directory.EnumerateDirectories(pluginsDirectory, "*" + RetiredSuffix))
                TryDelete(retired);
        }
        catch (Exception)
        {
            return said;
        }

        foreach (var pending in waiting)
        {
            var target = pending[..^PendingSuffix.Length];
            var name = Path.GetFileName(target);
            var retired = target + RetiredSuffix;
            var aside = false;
            try
            {
                if (Directory.Exists(target))
                {
                    Directory.Move(target, retired);
                    aside = true;
                }
                Directory.Move(pending, target);
                TryDelete(retired);
                said.Add($"Replaced {name} with the version that was waiting.");
            }
            catch (Exception ex)
            {
                // The old one moved aside but the new one did not move in: put the old one back
                // rather than leave no plugin at all.
                if (aside && !Directory.Exists(target))
                    TryMove(retired, target);
                said.Add($"{name} has a new version waiting, but the old one is still in use: {ex.Message}. It is swapped in when Meows next starts.");
            }
        }

        return said;
    }

    private static void TryMove(string from, string to)
    {
        try
        {
            Directory.Move(from, to);
        }
        catch (Exception)
        {
            // Nothing more to do from here; the folder is still there under its other name.
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception)
        {
            // Skipped by the scan and tried again next time.
        }
    }

    /// <summary>
    /// The one top-level folder every entry sits under, with its slash, or empty when the files
    /// are at the root. A zipped folder has one; a zipped set of files has none.
    /// </summary>
    private static string CommonFolder(List<string> paths)
    {
        string? folder = null;
        foreach (var path in paths)
        {
            var slash = path.IndexOf('/');
            if (slash <= 0)
                return "";
            var top = path[..(slash + 1)];
            if (folder is null)
                folder = top;
            else if (!string.Equals(folder, top, StringComparison.OrdinalIgnoreCase))
                return "";
        }
        return folder ?? "";
    }

    /// <summary>
    /// Which DLL is the plugin. A build folder holds one <c>Name.deps.json</c>, for the project
    /// that was built, and none for the libraries it pulled in, so that name is the answer. With
    /// no deps file, a folder of exactly one DLL is unambiguous; anything else is not.
    /// </summary>
    private static string? PluginName(List<string> paths, string prefix)
    {
        var atRoot = paths
            .Where(p => p.Length > prefix.Length && !p[prefix.Length..].Contains('/'))
            .Select(p => p[prefix.Length..])
            .ToList();

        var deps = atRoot.Where(n => n.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)).ToList();
        if (deps.Count == 1)
        {
            var name = deps[0][..^".deps.json".Length];
            if (atRoot.Any(n => n.Equals(name + ".dll", StringComparison.OrdinalIgnoreCase)))
                return name;
        }

        var dlls = atRoot.Where(n => n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToList();
        return dlls.Count == 1 ? dlls[0][..^".dll".Length] : null;
    }
}
