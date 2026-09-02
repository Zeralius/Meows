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
