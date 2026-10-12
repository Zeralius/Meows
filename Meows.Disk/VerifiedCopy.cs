using System.Security.Cryptography;

namespace Meows.Disk;

/// <summary>
/// One file copied with proof: the source hashed as it is read, the copy read back from the disk
/// and hashed again, and the two compared. Carry, Rehome and Nest each had their own version of
/// this, which is the kind of copy <see cref="ContentHash"/> warns drifts apart; this is the one
/// all three use now.
/// </summary>
public static class VerifiedCopy
{
    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="target"/> and says whether the copy on
    /// disk is byte for byte the source. Dates come along, and the attributes that say something
    /// about a file when asked to; a stamp that cannot be set does not fail the copy.
    /// </summary>
    /// <param name="mode">
    /// <see cref="FileMode.CreateNew"/> to refuse a file already there, <see cref="FileMode.Create"/>
    /// to write over it.
    /// </param>
    /// <param name="share">
    /// What others may do to the source meanwhile. <see cref="FileShare.Read"/> refuses a file
    /// being written; <see cref="FileShare.ReadWrite"/> copies it anyway, and the comparison
    /// catches it if it changed underneath.
    /// </param>
    public static bool Copy(string source, string target, CancellationToken token = default,
        FileMode mode = FileMode.CreateNew, FileShare share = FileShare.Read, bool attributes = false, byte[]? buffer = null)
    {
        buffer ??= new byte[1024 * 1024];
        var info = new FileInfo(source);

        string sourceHash;
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, share, buffer.Length))
        using (var output = new FileStream(target, mode, FileAccess.Write, FileShare.None, buffer.Length))
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
                hash.AppendData(buffer, 0, read);
            }
            sourceHash = Convert.ToHexString(hash.GetHashAndReset());
        }

        try
        {
            File.SetLastWriteTimeUtc(target, info.LastWriteTimeUtc);
            File.SetCreationTimeUtc(target, info.CreationTimeUtc);
            if (attributes)
                File.SetAttributes(target, info.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive));
        }
        catch (Exception)
        {
            // The bytes matter more than the stamps.
        }

        using var check = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length);
        using var again = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int n;
        while ((n = check.Read(buffer, 0, buffer.Length)) > 0)
            again.AppendData(buffer, 0, n);
        return Convert.ToHexString(again.GetHashAndReset()) == sourceHash;
    }
}
