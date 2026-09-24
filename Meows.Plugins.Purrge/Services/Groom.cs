using System.Globalization;
using System.Text.RegularExpressions;

namespace Meows.Plugins.Purrge.Services;

public enum GroomCase
{
    Unchanged,
    Lower,
    Upper,
    Title,
}

/// <summary>
/// What a rename does to a name, in the order it is written here: the browser's (1) comes off,
/// find is replaced, the case changes, then the template puts the name together with a number or
/// a date. The extension is never touched; a name that lies about its kind is Portion's to fix.
/// </summary>
public sealed record GroomRule
{
    /// <summary>Which files, as a wildcard pattern on the name. Empty is every file.</summary>
    public string Filter { get; init; } = "";

    public bool DropCopyCounter { get; init; }

    public string Find { get; init; } = "";

    public string Replace { get; init; } = "";

    public bool UseRegex { get; init; }

    public GroomCase Case { get; init; }

    /// <summary>The new name without its extension: {name} is what the steps above left, {n} the number, {date} the file's date.</summary>
    public string Template { get; init; } = "{name}";

    public int Start { get; init; } = 1;

    public int Digits { get; init; } = 3;
}

public enum GroomProblem
{
    None,

    /// <summary>Another file already has the new name and is not moving out of the way.</summary>
    Taken,

    /// <summary>Two files in this run would end up with the same name.</summary>
    Twice,

    /// <summary>Empty, or holding a character Windows refuses in a file name.</summary>
    Invalid,

    /// <summary>The regular expression does not parse.</summary>
    BadPattern,
}

/// <summary>One file in the preview: what it is called, what it would be called, and whether that can happen.</summary>
public sealed record GroomRow(string Folder, string From, string To, GroomProblem Problem)
{
    public bool Changes => !string.Equals(From, To, StringComparison.Ordinal);
}

/// <summary>A run that happened, kept so it can be taken back: each pair is the name before and after.</summary>
public sealed record GroomRun(string Folder, IReadOnlyList<GroomMove> Moves, DateTime WhenUtc);

public sealed record GroomMove(string From, string To);

/// <summary>What happened when a run or its undo was carried out.</summary>
public sealed record GroomOutcome(int Moved, IReadOnlyList<string> Failed);

/// <summary>
/// Groom, the bulk renamer. Purrge found the duplicates and showed how they got there: a (1)
/// re-download, a transposed digit, a hash-named re-save. This tidies the names, with every result
/// on screen before anything moves, a clash stopping the whole run rather than one file, and the
/// last run kept so it can be undone.
/// </summary>
public static class Groom
{
    private static readonly Regex CopyCounter = new(@"\s*(\(\d{1,3}\)|- Copy( \(\d{1,3}\))?|- Kopie( \(\d{1,3}\))?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly char[] Forbidden = [.. Path.GetInvalidFileNameChars(), '<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    /// <summary>
    /// The preview for a folder. Only the files directly in it; a rename across a tree is a larger
    /// thing than this wants to be responsible for. Numbering follows the current names in order.
    /// </summary>
    public static IReadOnlyList<GroomRow> Plan(string folder, GroomRule rule)
    {
        List<FileInfo> files;
        try
        {
            files = new DirectoryInfo(folder).EnumerateFiles()
                .Where(f => !f.Attributes.HasFlag(FileAttributes.Hidden) && !f.Attributes.HasFlag(FileAttributes.System))
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }

        var chosen = files
            .Where(f => Matches(f.Name, rule.Filter))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Regex? pattern = null;
        if (rule.UseRegex && rule.Find.Length > 0)
        {
            try
            {
                pattern = new Regex(rule.Find, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
            }
            catch (ArgumentException)
            {
                return chosen.Select(f => new GroomRow(folder, f.Name, f.Name, GroomProblem.BadPattern)).ToList();
            }
        }

        var named = new List<(string From, string To)>();
        var number = rule.Start;
        foreach (var file in chosen)
        {
            named.Add((file.Name, NewName(file, rule, pattern, number)));
            number++;
        }

        // A name is taken when a file that stays put already has it. A file that is itself being
        // renamed away is no obstacle, which is what lets a run swap two names or shift a sequence.
        var moving = new HashSet<string>(named.Where(n => !Same(n.From, n.To)).Select(n => n.From), StringComparer.OrdinalIgnoreCase);
        var staying = new HashSet<string>(files.Select(f => f.Name).Where(n => !moving.Contains(n)), StringComparer.OrdinalIgnoreCase);
        var counts = named.Where(n => !string.Equals(n.From, n.To, StringComparison.Ordinal))
            .GroupBy(n => n.To, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        return named.Select(n =>
        {
            var problem = GroomProblem.None;
            if (!string.Equals(n.From, n.To, StringComparison.Ordinal))
            {
                if (!IsValid(n.To))
                    problem = GroomProblem.Invalid;
                else if (!Same(n.From, n.To) && staying.Contains(n.To))
                    problem = GroomProblem.Taken;
                else if (counts[n.To] > 1)
                    problem = GroomProblem.Twice;
            }
            return new GroomRow(folder, n.From, n.To, problem);
        }).ToList();
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string NewName(FileInfo file, GroomRule rule, Regex? pattern, int number)
    {
        var extension = file.Extension;
        var stem = Path.GetFileNameWithoutExtension(file.Name);

        if (rule.DropCopyCounter)
            stem = CopyCounter.Replace(stem, "");

        if (rule.Find.Length > 0)
        {
            try
            {
                stem = pattern is not null
                    ? pattern.Replace(stem, rule.Replace)
                    : stem.Replace(rule.Find, rule.Replace, StringComparison.OrdinalIgnoreCase);
            }
            catch (RegexMatchTimeoutException)
            {
            }
        }

        stem = rule.Case switch
        {
            GroomCase.Lower => stem.ToLowerInvariant(),
            GroomCase.Upper => stem.ToUpperInvariant(),
            GroomCase.Title => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(stem.ToLowerInvariant()),
            _ => stem,
        };

        var template = string.IsNullOrWhiteSpace(rule.Template) ? "{name}" : rule.Template;
        var digits = Math.Clamp(rule.Digits, 1, 9);
        stem = template
            .Replace("{name}", stem, StringComparison.OrdinalIgnoreCase)
            .Replace("{n}", number.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0'), StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", file.LastWriteTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

        return stem.Trim() + extension;
    }

    /// <summary>A name Windows would accept: not empty, no forbidden character, not ending in a dot or a space.</summary>
    public static bool IsValid(string name) =>
        !string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(name)) &&
        name.IndexOfAny(Forbidden) < 0 &&
        !name.EndsWith('.') && !name.EndsWith(' ');

    private static bool Matches(string name, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;
        return filter.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(pattern => Regex.IsMatch(name, "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase));
    }

    /// <summary>
    /// Carries a preview out. Refuses outright while any row has a problem. Every file first steps
    /// aside under a temporary name, then takes its new one, so a swap or a shifted sequence never
    /// meets itself halfway. Dates are kept, since a move within a folder does not touch them.
    /// </summary>
    public static (GroomOutcome Outcome, GroomRun? Run) Apply(IReadOnlyList<GroomRow> rows)
    {
        if (rows.Count == 0 || rows.Any(r => r.Problem != GroomProblem.None))
            return (new GroomOutcome(0, []), null);

        var folder = rows[0].Folder;
        var changing = rows.Where(r => r.Changes).Select(r => new GroomMove(r.From, r.To)).ToList();
        var (outcome, done) = Move(folder, changing);
        return (outcome, done.Count == 0 ? null : new GroomRun(folder, done, DateTime.UtcNow));
    }

    /// <summary>
    /// Takes a run back, as far as the files still let it: each goes back to its old name unless
    /// it has gone or something else now has that name.
    /// </summary>
    public static GroomOutcome Undo(GroomRun run)
    {
        var back = run.Moves.Select(m => new GroomMove(m.To, m.From)).ToList();
        var here = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in Directory.EnumerateFiles(run.Folder))
                here.Add(Path.GetFileName(file));
        }
        catch (Exception)
        {
            return new GroomOutcome(0, back.Select(b => b.From).ToList());
        }

        var leaving = new HashSet<string>(back.Select(b => b.From), StringComparer.OrdinalIgnoreCase);
        var possible = new List<GroomMove>();
        var failed = new List<string>();
        foreach (var move in back)
        {
            var blocked = here.Contains(move.To) && !leaving.Contains(move.To);
            if (!here.Contains(move.From) || blocked)
                failed.Add(move.From);
            else
                possible.Add(move);
        }

        var (outcome, _) = Move(run.Folder, possible);
        return outcome with { Failed = [.. failed, .. outcome.Failed] };
    }

    private static (GroomOutcome, List<GroomMove>) Move(string folder, IReadOnlyList<GroomMove> moves)
    {
        var parked = new List<(GroomMove Move, string Temporary)>();
        var failed = new List<string>();

        foreach (var move in moves)
        {
            var temporary = $".meows-groom-{Guid.NewGuid():N}{Path.GetExtension(move.From)}";
            try
            {
                File.Move(Path.Combine(folder, move.From), Path.Combine(folder, temporary));
                parked.Add((move, temporary));
            }
            catch (Exception)
            {
                failed.Add(move.From);
            }
        }

        var done = new List<GroomMove>();
        foreach (var (move, temporary) in parked)
        {
            try
            {
                File.Move(Path.Combine(folder, temporary), Path.Combine(folder, move.To));
                done.Add(move);
            }
            catch (Exception)
            {
                // Its new name turned out to be taken after all: it goes back to the old one
                // rather than being left under a name nobody chose.
                failed.Add(move.From);
                try
                {
                    File.Move(Path.Combine(folder, temporary), Path.Combine(folder, move.From));
                }
                catch (Exception)
                {
                }
            }
        }

        return (new GroomOutcome(done.Count, failed), done);
    }
}
