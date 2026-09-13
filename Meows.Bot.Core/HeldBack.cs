namespace Meows.Bot;

/// <summary>What became of a file put in Held_Back: where it went, or why it did not.</summary>
public sealed record HeldOutcome(bool Ok, string? Destination, string? Error);

/// <summary>
/// The move into a group's Held_Back folder, shared by Kibble at the click, Portion after a
/// walk and Telegram Poster on a queue row. Moves rather than deletes, keeps the file's own
/// date, and never overwrites what is already there.
/// </summary>
public static class HeldBack
{
    public static HeldOutcome SetAside(BotWorkspace workspace, GroupConfig group, string path)
    {
        try
        {
            var folder = workspace.HeldBackFolder(group);
            Directory.CreateDirectory(folder);

            var target = Unique(folder, Path.GetFileName(path));
            var written = File.GetLastWriteTimeUtc(path);

            File.Move(path, target);
            File.SetLastWriteTimeUtc(target, written);

            return new HeldOutcome(true, target, null);
        }
        catch (Exception ex)
        {
            return new HeldOutcome(false, null, ex.Message);
        }
    }

    /// <summary>Puts a held file back where it was taken from, if nothing has appeared there since.</summary>
    public static bool PutBack(string held, string original)
    {
        try
        {
            if (!File.Exists(held) || File.Exists(original))
                return false;
            Directory.CreateDirectory(Path.GetDirectoryName(original)!);
            File.Move(held, original);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The group whose queue a path sits in, or null when it is in none of them.</summary>
    public static GroupConfig? GroupOf(BotWorkspace workspace, BotConfig config, string path)
    {
        var folder = Path.GetDirectoryName(path);
        if (folder is null)
            return null;

        foreach (var group in config.Groups)
        {
            if (Same(workspace.ToSendFolder(group), folder))
                return group;
        }

        return null;
    }

    private static bool Same(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static string Unique(string folder, string fileName)
    {
        var candidate = Path.Combine(folder, fileName);
        if (!File.Exists(candidate))
            return candidate;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; ; i++)
        {
            candidate = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }
}
