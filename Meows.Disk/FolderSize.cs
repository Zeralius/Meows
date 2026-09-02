namespace Meows.Disk;

/// <summary>
/// How much room a folder is really costing, everything underneath included. Shared because
/// three plugins now ask the same question, and the awkward parts, unreadable folders and
/// reparse points, are the same awkward parts every time.
/// </summary>
public static class FolderSize
{
    public static long Of(string path, bool skipSystemFolders = false)
    {
        try
        {
            return Of(new DirectoryInfo(path), skipSystemFolders);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    public static long Of(DirectoryInfo folder, bool skipSystemFolders = false)
    {
        var total = 0L;
        var stack = new Stack<DirectoryInfo>();
        stack.Push(folder);

        while (stack.Count > 0)
        {
            var current = stack.Pop();

            try
            {
                foreach (var file in current.EnumerateFiles())
                {
                    try
                    {
                        total += file.Length;
                    }
                    catch (Exception)
                    {
                        // Vanished between the listing and the question. Not worth stopping for.
                    }
                }
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var child in FolderWalk.Into(current, skipSystemFolders))
                stack.Push(child);
        }

        return total;
    }

    public static string Humanise(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 * 1024 => $"{bytes / 1024d / 1024 / 1024 / 1024:0.##} TB",
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024 / 1024:0.##} GB",
        >= 1024 * 1024 => $"{bytes / 1024d / 1024:0.#} MB",
        >= 1024 => $"{bytes / 1024d:0} KB",
        _ => $"{bytes} B",
    };
}
