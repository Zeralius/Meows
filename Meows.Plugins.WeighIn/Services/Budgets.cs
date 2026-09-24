namespace Meows.Plugins.WeighIn.Services;

/// <summary>A line a folder is not meant to cross: Downloads at 20 GB, Recordings at 200 GB. Set by hand, never guessed.</summary>
public sealed class FolderBudget
{
    public string Path { get; set; } = "";

    public long Bytes { get; set; }
}

/// <summary>Where a budgeted folder stands in a reading.</summary>
/// <param name="Size">What the reading measured, or null when it did not reach the folder.</param>
public sealed record BudgetStanding(FolderBudget Budget, long? Size)
{
    public bool Measured => Size is not null;

    public bool IsOver => Size > Budget.Bytes;

    /// <summary>How far past the line, or how much room is left when negative.</summary>
    public long Over => (Size ?? 0) - Budget.Bytes;
}

/// <summary>
/// Folder budgets against the readings. A folder that is meant to be full, the games drive, is
/// simply not given one; a budget exists only because somebody set it, and a folder is over it
/// only on the day a reading says so.
/// </summary>
public static class Budgets
{
    /// <summary>The same folder, whatever the case and however many trailing separators.</summary>
    public static bool Same(string a, string b) =>
        string.Equals(Trim(a), Trim(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="path"/> is somewhere inside <paramref name="folder"/>.</summary>
    public static bool IsUnder(string path, string folder)
    {
        var inside = Trim(folder) + System.IO.Path.DirectorySeparatorChar;
        return Trim(path).StartsWith(inside, StringComparison.OrdinalIgnoreCase)
               || Trim(path).StartsWith(Trim(folder) + System.IO.Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string Trim(string path) =>
        path.Length > 3 ? path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) : path;

    /// <summary>Every budget against one reading, the ones over first and furthest over first.</summary>
    public static IReadOnlyList<BudgetStanding> Check(IEnumerable<FolderBudget> budgets, Reading? reading) =>
        budgets
            .Select(b => new BudgetStanding(b, reading?.SizeOf(b.Path)))
            .OrderByDescending(s => s.IsOver)
            .ThenByDescending(s => s.Over)
            .ToList();

    /// <summary>
    /// The budgets this reading finds over that the one before did not: the moment a folder
    /// crosses its line, which is worth a line in the history once rather than every day after.
    /// </summary>
    public static IReadOnlyList<BudgetStanding> Crossed(IEnumerable<FolderBudget> budgets, Reading now, Reading? before) =>
        Check(budgets, now)
            .Where(s => s.IsOver && !(before?.SizeOf(s.Budget.Path) > s.Budget.Bytes))
            .ToList();
}
