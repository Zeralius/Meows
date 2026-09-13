using System.Security.Cryptography;

namespace Meows.Disk;

/// <summary>
/// The staged content check: the first 64 KB, then the whole file only when that still agrees,
/// with the caller having compared sizes first. Most files are settled without being read in
/// full, which is what makes a whole-drive scan or a whole-backup check finish today.
///
/// Shared here because Purrge, its Compare mode and Kibble's intake all need the same thing,
/// and three copies of a hash is how one gets a fix and the others do not.
/// </summary>
public static class ContentHash
{
    public const int PartialBytes = 64 * 1024;

    /// <summary>A hash of the first <see cref="PartialBytes"/>, or null if the file cannot be read.</summary>
    public static string? Partial(string path) => Of(path, PartialBytes);

    /// <summary>A hash of the whole file, or null if the file cannot be read.</summary>
    public static string? Full(string path) => Of(path, null);

    /// <summary>
    /// Whether two files of the same size hold the same bytes. Null when one of them could not
    /// be read, which is a different answer from "no" and is reported as one.
    /// </summary>
    public static bool? Same(string a, string b)
    {
        var pa = Partial(a);
        var pb = Partial(b);
        if (pa is null || pb is null)
            return null;
        if (pa != pb)
            return false;

        var fa = Full(a);
        var fb = Full(b);
        if (fa is null || fb is null)
            return null;
        return fa == fb;
    }

    /// <summary>
    /// Hashing opens the file, and opening a file moves its last-access stamp. The stamp is put
    /// back afterwards: a duplicate sweep is not the file being used, and Catnip needs the truth.
    /// </summary>
    private static string? Of(string path, int? maxBytes) => AccessTime.Preserving(path, () => Read(path, maxBytes));

    private static string? Read(string path, int? maxBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
            using var sha = SHA256.Create();

            if (maxBytes is null)
                return Convert.ToHexString(sha.ComputeHash(stream));

            var buffer = new byte[maxBytes.Value];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, read)));
        }
        catch (Exception)
        {
            // Locked, gone, or unreadable. It just does not take part.
            return null;
        }
    }
}
