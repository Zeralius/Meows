using Mews.Disk;

namespace Mews.Plugins.Molt.Services;

/// <summary>Where the shed material goes.</summary>
public enum ShedMode
{
    /// <summary>Recoverable, but the room is not actually back until the bin is emptied.</summary>
    RecycleBin,

    /// <summary>Gone, and the room is back immediately.</summary>
    Permanent,
}

public sealed record ShedResult(int Removed, int Failed, long Freed, string? FailureReason)
{
    public bool Succeeded => Failed == 0 && FailureReason is null;
}

/// <summary>
/// Removes what the catalogue found.
///
/// The two modes exist because neither answer is right on its own. Everything else in Mews goes
/// to the Recycle Bin, on the principle that an automated judgement should stay reversible. But
/// a bin is still on the disk, so shedding twenty gigabytes into it frees nothing until it is
/// emptied, which is the opposite of the point.
///
/// The way out is that a cache is defined by being rebuildable. Its safety does not come from
/// the bin, it comes from the tool that made it being willing to make it again. So permanent is
/// offered and honest about itself, while the bin stays the default, because the person doing it
/// should be the one choosing which guarantee they want.
/// </summary>
public static class Shedder
{
    public static ShedResult Shed(IReadOnlyList<string> paths, ShedMode mode, long expected)
    {
        if (paths.Count == 0)
            return new ShedResult(0, 0, 0, null);

        return mode == ShedMode.RecycleBin ? ToBin(paths, expected) : Permanently(paths, expected);
    }

    private static ShedResult ToBin(IReadOnlyList<string> paths, long expected)
    {
        var outcome = RecycleBin.Send(paths);
        return new ShedResult(outcome.Deleted, outcome.Failed, expected, outcome.FailureReason);
    }

    /// <summary>
    /// One at a time, because a cache always has a handful of files something still has open,
    /// and one locked file must not stop the other nine thousand from going.
    /// </summary>
    private static ShedResult Permanently(IReadOnlyList<string> paths, long expected)
    {
        var removed = 0;
        var failed = 0;

        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                    removed++;
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                    removed++;
                }
            }
            catch (Exception)
            {
                // In use, or not ours to remove. Both are ordinary here.
                failed++;
            }
        }

        var reason = failed == 0
            ? null
            : $"{failed} item(s) were in use and stayed where they are.";

        return new ShedResult(removed, failed, expected, reason);
    }
}
