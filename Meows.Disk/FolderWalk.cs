namespace Meows.Disk;

/// <summary>
/// Stepping from a folder to what is inside it, in one place.
///
/// Six scanners used to do this themselves, each with its own enumeration and error handling.
/// That was fine until a folder named " " sent Chonk into an infinite loop while three others
/// walked past it, purely because of how each held the path. Everything goes through here now so
/// a fix lands everywhere at once.
/// </summary>
public static class FolderWalk
{
    /// <summary>
    /// Subfolders worth walking into. An unreadable folder gives an empty list rather than
    /// throwing, because one locked directory should not abandon a whole drive scan.
    /// </summary>
    public static IReadOnlyList<DirectoryInfo> Into(DirectoryInfo directory, bool skipSystemFolders = true)
    {
        DirectoryInfo[] children;
        try
        {
            children = directory.GetDirectories();
        }
        catch (Exception)
        {
            return [];
        }

        var worth = new List<DirectoryInfo>(children.Length);

        foreach (var child in children)
        {
            if (!WalkRules.ShouldDescend(child, skipSystemFolders))
                continue;

            // Windows strips a trailing space or dot from a path, so a folder named " " can
            // resolve back to its own parent and the walk never terminates.
            if (!WalkRules.LeadsSomewhereNew(child, directory))
                continue;

            worth.Add(child);
        }

        return worth;
    }

    /// <summary>
    /// Files in this folder, or an empty array if it cannot be read. Same deal as Into: skip it
    /// rather than fail the whole scan.
    /// </summary>
    public static FileInfo[] Files(DirectoryInfo directory)
    {
        try
        {
            return directory.GetFiles();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Whether the folder can be read at all. Not the same question as whether it is empty, and
    /// a scanner that confuses the two will offer to delete something it never looked inside.
    /// </summary>
    public static bool CanRead(DirectoryInfo directory)
    {
        try
        {
            directory.GetDirectories();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
