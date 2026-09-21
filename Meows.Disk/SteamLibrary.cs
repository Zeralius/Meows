using System.Text.RegularExpressions;

namespace Meows.Disk;

/// <summary>What Steam records about an installed game.</summary>
public sealed record SteamGame(string Name, long SizeOnDisk, DateTime? LastPlayed, string ManifestPath)
{
    /// <summary>
    /// Steam writes 0 for a game that has never been launched, and omits the key entirely for
    /// some entries. Only the first of those means "never played".
    /// </summary>
    public bool NeverPlayed => LastPlayed == DateTime.UnixEpoch;

    public bool PlayedUnknown => LastPlayed is null;
}

/// <summary>
/// Reads Steam's own records instead of guessing from the filesystem. Every installed game has an
/// appmanifest holding its real name, size and last played time, which is better than anything we
/// could infer. No API and no network needed: they are plain text files already on disk.
/// </summary>
public static class SteamLibrary
{
    /// <summary>
    /// The game installed at this folder, or null when it is not a Steam game folder.
    ///
    /// A game lives at <c>...\steamapps\common\&lt;installdir&gt;</c>, and the manifest naming it
    /// sits two levels up in <c>steamapps</c>. Finding the pair is what makes this reliable rather
    /// than a guess based on the folder being inside something called Steam.
    /// </summary>
    public static SteamGame? GameAt(string folder)
    {
        try
        {
            var directory = new DirectoryInfo(folder.TrimEnd(Path.DirectorySeparatorChar));
            var common = directory.Parent;

            if (common is null || !common.Name.Equals("common", StringComparison.OrdinalIgnoreCase))
                return null;

            var steamapps = common.Parent;
            if (steamapps is null || !steamapps.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase))
                return null;

            foreach (var manifest in steamapps.EnumerateFiles("appmanifest_*.acf"))
            {
                var text = File.ReadAllText(manifest.FullName);
                if (!Value(text, "installdir").Equals(directory.Name, StringComparison.OrdinalIgnoreCase))
                    continue;

                var name = Value(text, "name");
                return new SteamGame(
                    name.Length > 0 ? name : directory.Name,
                    Number(text, "SizeOnDisk") ?? 0,
                    Played(text),
                    manifest.FullName);
            }
        }
        catch (Exception)
        {
            // Unreadable manifest or a path that will not resolve. Saying nothing is correct.
        }

        return null;
    }

    /// <summary>
    /// Every library folder Steam knows, from its own libraryfolders.vdf: the one under the
    /// client first, then the others on whatever drives they were made on. Empty when Steam is
    /// not installed or the file cannot be read.
    /// </summary>
    public static IReadOnlyList<string> LibraryFolders()
    {
        var client = ClientFolder();
        if (client is null)
            return [];
        var folders = new List<string> { client };
        try
        {
            var vdf = Path.Combine(client, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                foreach (Match match in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
                {
                    var path = match.Groups[1].Value.Replace("\\\\", "\\");
                    if (path.Length > 0 && !folders.Contains(path, StringComparer.OrdinalIgnoreCase))
                        folders.Add(path);
                }
            }
        }
        catch (Exception)
        {
            // The client folder alone is still an answer.
        }
        return folders;
    }

    /// <summary>
    /// Where a game is installed, by its app id, from the manifest in whichever library holds
    /// it: <c>&lt;library&gt;\steamapps\common\&lt;installdir&gt;</c>. Null when no library has it.
    /// The uninstall registry often leaves a Steam game's location blank; this is the answer it
    /// should have given.
    /// </summary>
    public static string? InstallFolderOf(string appId)
    {
        foreach (var library in LibraryFolders())
        {
            try
            {
                var manifest = Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf");
                if (!File.Exists(manifest))
                    continue;
                var installdir = Value(File.ReadAllText(manifest), "installdir");
                if (installdir.Length > 0)
                    return Path.Combine(library, "steamapps", "common", installdir);
            }
            catch (Exception)
            {
                // A library on a drive that is not there today.
            }
        }
        return null;
    }

    /// <summary>Where the Steam client is, from the registry, or the default when the registry does not say.</summary>
    private static string? ClientFolder()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")
                            ?? Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
            if (key?.GetValue("InstallPath") is string path && Directory.Exists(path))
                return path;
        }
        catch (Exception)
        {
            // Fall through to the default.
        }
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        return Directory.Exists(fallback) ? fallback : null;
    }

    private static DateTime? Played(string text)
    {
        var seconds = Number(text, "LastPlayed");
        return seconds is null ? null : DateTime.UnixEpoch.AddSeconds(seconds.Value);
    }

    private static long? Number(string text, string key) =>
        long.TryParse(Value(text, key), out var parsed) ? parsed : null;

    /// <summary>
    /// Pulls one value out of the flat part of an acf file. The format nests, but every key this
    /// cares about lives at the top level, so matching the quoted pair is enough and avoids
    /// writing a parser for a format that would only ever be read here.
    /// </summary>
    private static string Value(string text, string key)
    {
        var match = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s+\"([^\"]*)\"",
            RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

        return match.Success ? match.Groups[1].Value : "";
    }
}
