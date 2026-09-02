namespace Meows.Disk;

/// <summary>
/// The one place that steps from a folder to what is inside it.
///
/// Six scanners were each doing this by hand: their own enumeration, their own try/catch, their
/// own idea of which children to refuse. They agreed until they did not. A folder named " " sent
/// Chonk round in circles past five million entries while Mouser, Molt and Purrge walked straight
/// past it, purely because of how each happened to hold a path. That is not a difference anybody
/// chose, and it is the kind that is invisible until a scan does not stop.
///
/// Everything that walks a tree goes through here now, so a rule learned once is a rule
/// everywhere.
/// </summary>
public static class FolderWalk
{
    /// <summary>
    /// The subfolders worth stepping into. Unreadable folders come back as nothing rather than
    /// as an exception, since one locked directory is not a reason to abandon a drive.
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

            // A child that resolves back to where we already are is not a child. Windows drops a
            // trailing space or dot from a path, so a folder named " " can hand back its own
            // parent and the walk never ends.
            if (!WalkRules.LeadsSomewhereNew(child, directory))
                continue;

            worth.Add(child);
        }

        return worth;
    }

    /// <summary>
    /// The files in this folder, or nothing if it cannot be read. Same bargain as above: a folder
    /// that refuses to open contributes nothing rather than stopping everything.
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
    /// Whether the folder could be read at all, which is a different question from whether it is
    /// empty. A scanner that cannot tell the two apart will eventually offer to delete something
    /// it never looked inside.
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
