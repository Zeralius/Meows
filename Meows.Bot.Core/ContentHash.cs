namespace Meows.Bot;

/// <summary>
/// Content hashing for "have I got this already" checks.
///
/// The hashing itself lives in <see cref="Disk.ContentHash"/>, shared with Purrge. What is here
/// is the one question Kibble asks of it, on every click, against a group queue that can hold
/// several hundred files: comparing sizes first means most of them never get opened.
/// </summary>
public static class ContentHash
{
    /// <summary>Full SHA-256, or null if the file cannot be read.</summary>
    public static string? Of(string path) => Disk.ContentHash.Full(path);

    /// <summary>Hash of the first block only, used to rule files out cheaply.</summary>
    public static string? Partial(string path) => Disk.ContentHash.Partial(path);

    /// <summary>
    /// Is this file already somewhere in <paramref name="candidates"/>? Size first, then the
    /// first block, then the whole thing, so a folder of a few hundred files costs almost
    /// nothing unless there is a real match.
    /// </summary>
    public static string? FindMatch(string path, IEnumerable<string> candidates)
    {
        long size;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch (Exception)
        {
            return null;
        }

        var sameSize = new List<string>();
        foreach (var candidate in candidates)
        {
            try
            {
                if (new FileInfo(candidate).Length == size)
                    sameSize.Add(candidate);
            }
            catch (Exception)
            {
                // Vanished or unreadable, so it cannot be a match we care about.
            }
        }

        if (sameSize.Count == 0)
            return null;

        var partial = Partial(path);
        if (partial is null)
            return null;

        var stillPossible = sameSize.Where(c => Partial(c) == partial).ToList();
        if (stillPossible.Count == 0)
            return null;

        var full = Of(path);
        return full is null ? null : stillPossible.FirstOrDefault(c => Of(c) == full);
    }
}
