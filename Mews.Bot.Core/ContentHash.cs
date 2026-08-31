using System.Security.Cryptography;

namespace Mews.Bot;

/// <summary>
/// Content hashing for "have I got this already" checks.
///
/// Same staged idea Purrge uses, for the same reason: comparing sizes first means most files
/// never get opened. Here it matters because the check runs on every click, against a group
/// queue that can hold several hundred files.
/// </summary>
public static class ContentHash
{
    private const int PartialBytes = 64 * 1024;

    /// <summary>Full SHA-256, or null if the file cannot be read.</summary>
    public static string? Of(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Hash of the first block only, used to rule files out cheaply.</summary>
    public static string? Partial(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
            var buffer = new byte[PartialBytes];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, read)));
        }
        catch (Exception)
        {
            return null;
        }
    }

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
