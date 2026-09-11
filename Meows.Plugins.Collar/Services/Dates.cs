namespace Meows.Plugins.Collar.Services;

/// <summary>What sort of date it is. Only ever used to pick a word and an icon.</summary>
public enum Kind
{
    Warranty,
    Insurance,
    Inspection,
    Subscription,
    Service,
    Document,
    Other,
}

/// <summary>How close it is.</summary>
public enum Standing
{
    Overdue,
    Soon,
    Later,
    Done,
}

/// <summary>
/// One date worth being reminded about, as it is stored.
///
/// A mutable class rather than a record, because this is what the settings file holds and it is
/// edited field by field on screen. Everything about it survives a restart, including the id,
/// which is what a row is found by after the list has been resorted underneath it.
/// </summary>
public sealed class CollarEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Title { get; set; } = "";

    public Kind Kind { get; set; } = Kind.Warranty;

    /// <summary>The date itself. Time of day is never part of it.</summary>
    public DateTime Due { get; set; } = DateTime.Today;

    /// <summary>Months between one and the next. Zero means it happens once.</summary>
    public int RepeatMonths { get; set; }

    public string Note { get; set; } = "";

    /// <summary>The receipt, the policy, the invoice. Optional, and never copied anywhere.</summary>
    public string? File { get; set; }

    /// <summary>Only ever true for something that happens once.</summary>
    public bool Done { get; set; }
}

/// <summary>
/// The arithmetic behind the list: how close a date is, and where the next one lands.
///
/// Pure, and told what today is rather than reading the clock, so a test can sit on a date and a
/// pass that starts before midnight cannot answer differently to the one after it.
/// </summary>
public static class Dates
{
    /// <summary>Anything further out than this is not news yet.</summary>
    public const int DefaultLead = 30;

    public static Standing Of(CollarEntry entry, DateTime today, int leadDays)
    {
        if (entry.Done && entry.RepeatMonths == 0)
            return Standing.Done;

        var due = entry.Due.Date;
        if (due < today.Date)
            return Standing.Overdue;

        return due <= today.Date.AddDays(Math.Max(0, leadDays)) ? Standing.Soon : Standing.Later;
    }

    public static int DaysLeft(CollarEntry entry, DateTime today) =>
        (int)(entry.Due.Date - today.Date).TotalDays;

    /// <summary>
    /// Dealt with. Something that repeats moves to its next date, something that happens once is
    /// finished with.
    ///
    /// The next date is walked forward until it is actually in the future, so a yearly thing that
    /// went unopened for two years lands on this year rather than on the one already missed. It
    /// keeps the day of the month it was set on, because that is what the insurer uses.
    /// </summary>
    public static void Handle(CollarEntry entry, DateTime today)
    {
        if (entry.RepeatMonths <= 0)
        {
            entry.Done = true;
            return;
        }

        var next = entry.Due.Date;
        var guard = 0;

        do
        {
            next = next.AddMonths(entry.RepeatMonths);
            guard++;
        }
        while (next <= today.Date && guard < 1000);

        entry.Due = next;
        entry.Done = false;
    }

    /// <summary>
    /// The order the list is read in: what is late first, then what is closest, then the ones
    /// already dealt with. A date that has passed is the only thing on the tab that needs doing
    /// today, so it goes at the top whatever else is in there.
    /// </summary>
    public static IEnumerable<CollarEntry> InOrder(IEnumerable<CollarEntry> entries, DateTime today, int leadDays) =>
        entries
            .OrderBy(e => Of(e, today, leadDays) == Standing.Done ? 1 : 0)
            .ThenBy(e => e.Due.Date)
            .ThenBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase);

    /// <summary>The key for a word rather than the word. Nothing here knows any language.</summary>
    public static string Describe(Kind kind) => "collar.kind." + kind.ToString().ToLowerInvariant();
}

/// <summary>
/// What can be worked out from a file that was dropped on the tab.
///
/// A guess, and a rough one. The point is that adding an entry costs a drag and a glance rather
/// than filling in a form, because a list that has to be typed into is a list nobody keeps up.
/// </summary>
public static class Receipt
{
    /// <summary>Two years, which is the statutory warranty on anything bought in Germany.</summary>
    public const int WarrantyMonths = 24;

    public static CollarEntry From(string path, DateTime today)
    {
        var name = Path.GetFileNameWithoutExtension(path) ?? "";
        var bought = today;

        try
        {
            var written = System.IO.File.GetLastWriteTime(path);

            // A file dated in the future is a clock that disagrees, not a receipt from next year.
            if (written.Year > 1990 && written.Date <= today.Date)
                bought = written.Date;
        }
        catch (Exception)
        {
            // An unreadable timestamp is not worth refusing the entry over.
        }

        return new CollarEntry
        {
            Title = Title(name),
            Kind = Kind.Warranty,
            Due = bought.AddMonths(WarrantyMonths),
            Note = "",
            File = path,
        };
    }

    /// <summary>
    /// A file name as a title: separators become spaces, and a leading date is dropped because it
    /// is already the date on the entry.
    /// </summary>
    public static string Title(string fileName)
    {
        var spaced = new System.Text.StringBuilder();
        foreach (var c in fileName)
            spaced.Append(c is '_' or '-' or '.' ? ' ' : c);

        var words = spaced.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        while (words.Count > 1 && words[0].All(char.IsDigit) && words[0].Length is 4 or 6 or 8)
            words.RemoveAt(0);

        var title = string.Join(" ", words.Select(Cased));
        return title.Length > 80 ? title[..80] : title;
    }

    /// <summary>
    /// A file name is usually all lower case and a title is not. A word that already has a
    /// capital in it is left alone, so an acronym or a model number keeps its shape.
    /// </summary>
    private static string Cased(string word) =>
        word.Length > 0 && !word.Any(char.IsUpper)
            ? char.ToUpperInvariant(word[0]) + word[1..]
            : word;
}
