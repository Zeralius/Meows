using Mews.Bot;

namespace Mews.Plugins.Kibble.Services;

/// <summary>Which timestamp a newly queued file should carry.</summary>
public enum IntakeStamp
{
    /// <summary>Keep the source file's time, so genuinely older art posts first.</summary>
    KeepSource,

    /// <summary>Stamp the moment it was queued, so it is first in, first out.</summary>
    QueuedNow,
}

public enum IntakeOutcome
{
    Sent,
    AlreadyInGroup,
    NotPostable,
    EmptyComic,
    Failed,
}

public sealed record IntakeResult(IntakeOutcome Outcome, string SourcePath, string? Destination, string? Detail)
{
    public bool Moved => Outcome == IntakeOutcome.Sent;
}

/// <summary>
/// Moving a file into a group's queue, with the checks that are only useful at the moment you
/// click. Everything here happens before the file lands, so a rejected file stays where it was
/// and the grid still shows it.
/// </summary>
public static class Intake
{
    /// <summary>
    /// Checks a file against a destination without moving anything, so the UI can grey out or
    /// warn before the click. Null means it is fine to send.
    /// </summary>
    public static IntakeResult? Inspect(string source, BotWorkspace workspace, GroupConfig group)
    {
        if (!MediaRules.IsPostable(source))
            return new IntakeResult(IntakeOutcome.NotPostable, source, null,
                "The bot does not recognise this extension, so it would sit in the queue being skipped.");

        if (MediaRules.IsComic(source) && MediaRules.ComicPages(source, group.ComicOrder ?? "name").Count == 0)
            return new IntakeResult(IntakeOutcome.EmptyComic, source, null,
                "No postable pages inside this archive, so sending it would fail.");

        // Against the queue and the archive both: re-sending something already posted is the
        // mistake that actually shows.
        var existing = workspace.Scan(workspace.ToSendFolder(group))
            .Concat(workspace.Scan(workspace.AlreadySentFolder(group), recursive: true));

        var match = ContentHash.FindMatch(source, existing);
        if (match is not null)
        {
            var where = match.Contains(Path.DirectorySeparatorChar + "Already_Sent" + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase)
                ? "already posted to this group"
                : "already in this group's queue";
            return new IntakeResult(IntakeOutcome.AlreadyInGroup, source, null,
                $"{where}, as {Path.GetFileName(match)}.");
        }

        return null;
    }

    /// <summary>Moves the file into the group's To_Send, having checked it first.</summary>
    public static IntakeResult Send(string source, BotWorkspace workspace, GroupConfig group, IntakeStamp stamp)
    {
        var problem = Inspect(source, workspace, group);
        if (problem is not null)
            return problem;

        try
        {
            var folder = workspace.ToSendFolder(group);
            Directory.CreateDirectory(folder);

            var target = UniquePath(folder, Path.GetFileName(source));
            var sourceWritten = File.GetLastWriteTimeUtc(source);

            File.Move(source, target);

            // The bot orders the queue by modified time, so this is not cosmetic. Import a
            // few hundred files with the clock stamped and "oldest" stops meaning anything.
            File.SetLastWriteTimeUtc(target, stamp == IntakeStamp.KeepSource ? sourceWritten : DateTime.UtcNow);

            return new IntakeResult(IntakeOutcome.Sent, source, target, null);
        }
        catch (Exception ex)
        {
            return new IntakeResult(IntakeOutcome.Failed, source, null, ex.Message);
        }
    }

    /// <summary>Puts a file back where it came from, for undo.</summary>
    public static bool Undo(IntakeResult result)
    {
        if (!result.Moved || result.Destination is null)
            return false;

        try
        {
            if (!File.Exists(result.Destination) || File.Exists(result.SourcePath))
                return false;
            Directory.CreateDirectory(Path.GetDirectoryName(result.SourcePath)!);
            File.Move(result.Destination, result.SourcePath);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Never overwrite. A name clash between two different files is entirely possible when
    /// pulling from several sources, and losing one silently would be the worst outcome here.
    /// </summary>
    private static string UniquePath(string folder, string fileName)
    {
        var candidate = Path.Combine(folder, fileName);
        if (!File.Exists(candidate))
            return candidate;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var n = 2; n < 10_000; n++)
        {
            candidate = Path.Combine(folder, $"{stem}_{n}{ext}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(folder, $"{stem}_{Guid.NewGuid():N}{ext}");
    }
}
