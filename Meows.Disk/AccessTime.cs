namespace Meows.Disk;

/// <summary>
/// Reading a file to look at it is not using it, and the last-access time should say so.
///
/// Windows tracks when a file was last opened, and that stamp is the only record of what was
/// downloaded and never looked at, which is the question Catnip in IDEAS.md wants to ask. A
/// Purrge sweep opens every file to hash it, and one sweep would flatten that history to "all
/// touched today". So every scanner in Meows that opens a file for its own purposes puts the
/// stamp back afterwards, through here, and the history stays true for whoever asks next.
/// </summary>
public static class AccessTime
{
    /// <summary>The stamp as it was, or null when it could not be read, in which case there is nothing to restore.</summary>
    public static DateTime? Remember(string path)
    {
        try
        {
            // A missing file answers with 1601 rather than throwing, and 1601 is not a stamp
            // worth putting back on anything.
            return File.Exists(path) ? File.GetLastAccessTimeUtc(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Puts a remembered stamp back. Quietly does nothing when there was none or it cannot be set.</summary>
    public static void Restore(string path, DateTime? remembered)
    {
        if (remembered is not { } when)
            return;

        try
        {
            File.SetLastAccessTimeUtc(path, when);
        }
        catch (Exception)
        {
            // Read-only media, a file that vanished, no permission. The read itself worked;
            // the stamp is a courtesy to the next reader, not a requirement of this one.
        }
    }

    /// <summary>Runs a read and leaves the stamp as it found it.</summary>
    public static T Preserving<T>(string path, Func<T> read)
    {
        var remembered = Remember(path);
        try
        {
            return read();
        }
        finally
        {
            Restore(path, remembered);
        }
    }
}
