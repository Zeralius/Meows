namespace Meows.Disk;

/// <summary>
/// Which folders a scan should walk into. Shared rather than copied per plugin, so adding a new
/// folder to skip does not have to be remembered in five places.
/// </summary>
public static class WalkRules
{
    /// <summary>
    /// Folders that are either not the user's, not interesting, or duplicated on purpose.
    /// Skipping them is what makes a whole drive scan finish in reasonable time.
    /// </summary>
    public static readonly string[] SkippedFolderNames =
    [
        "$Recycle.Bin", "System Volume Information", "Windows", "Program Files",
        "Program Files (x86)", "ProgramData", "node_modules", ".git", "obj", "bin",
    ];

    /// <summary>
    /// Whether to descend into this directory. Reparse points are skipped: a junction pointing
    /// at one of its own parents would loop forever.
    /// </summary>
    public static bool ShouldDescend(DirectoryInfo directory, bool skipSystemFolders = true)
    {
        try
        {
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return false;
        }
        catch (Exception)
        {
            // Cannot even read the attributes, so leave it alone.
            return false;
        }

        return !skipSystemFolders ||
               !SkippedFolderNames.Contains(directory.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether stepping into this child goes anywhere new.
    ///
    /// Windows strips a trailing space or dot from a path, so a folder named " " resolves back to
    /// its own parent and the walk loops between the two. There is a real one inside a Steam game
    /// called TOK 2; it took a Chonk scan past five million folders before anyone noticed.
    /// </summary>
    public static bool LeadsSomewhereNew(DirectoryInfo child, DirectoryInfo parent)
    {
        try
        {
            var from = parent.FullName.TrimEnd(Path.DirectorySeparatorChar);
            var to = child.FullName.TrimEnd(Path.DirectorySeparatorChar);

            return !to.Equals(from, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether a path still names the same thing after Windows normalises it.
    ///
    /// Names ending in a space or a dot do not: "data." becomes "data", so with both on disk an
    /// operation aimed at one hits the other. Deleting "data." really does remove "data" and
    /// everything in it, and report success.
    ///
    /// Only the last segment is compared, so relative paths are not refused for being relative.
    /// </summary>
    public static bool SurvivesNormalising(string path)
    {
        try
        {
            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var before = Path.GetFileName(trimmed);

            // A drive or share root has no last segment to lose.
            if (before.Length == 0)
                return true;

            var full = Path.GetFullPath(trimmed)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return Path.GetFileName(full).Equals(before, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
