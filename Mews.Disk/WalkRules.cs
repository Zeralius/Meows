namespace Mews.Disk;

/// <summary>
/// Which folders a scan has any business walking into. Shared because Purrge and Chonk are
/// asking different questions about the same disk, and two copies of this list would quietly
/// disagree the first time one of them learned about a new folder worth skipping.
/// </summary>
public static class WalkRules
{
    /// <summary>
    /// Folders whose contents are either not yours, not interesting, or supposed to be
    /// duplicated. Skipping them is what keeps a whole drive scan usable.
    /// </summary>
    public static readonly string[] SkippedFolderNames =
    [
        "$Recycle.Bin", "System Volume Information", "Windows", "Program Files",
        "Program Files (x86)", "ProgramData", "node_modules", ".git", "obj", "bin",
    ];

    /// <summary>
    /// Whether to descend into this directory. Reparse points are refused because a junction
    /// pointing at one of its own parents sends the walk round in circles forever.
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
            // Cannot even read the attributes, so there is nothing safe to do but leave it.
            return false;
        }

        return !skipSystemFolders ||
               !SkippedFolderNames.Contains(directory.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether stepping into this child actually goes anywhere new.
    ///
    /// Windows quietly strips a trailing space or dot from a path, so a folder genuinely named
    /// " " resolves back to its own parent. Enumerate the parent, take the child, ask the system
    /// where it is, and you are handed the parent again. Nothing about the folder looks wrong on
    /// the way past, and a walk goes round the pair forever.
    ///
    /// One of these exists inside a Steam game called TOK 2, and it took a Chonk scan to 45 TB
    /// across five million folders on a machine holding eighteen.
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
    /// Whether a path still names the same thing after Windows has normalised it.
    ///
    /// It does not when the name ends in a space or a dot: "data." normalises to "data", and if
    /// both exist side by side then an operation aimed at the first lands on the second. That is
    /// not theoretical. Deleting "data." really does take "data" and everything in it, and report
    /// success for having done so.
    ///
    /// Only the last segment is compared, so an ordinary relative path is not refused for the
    /// unrelated crime of being relative.
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
