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
}
