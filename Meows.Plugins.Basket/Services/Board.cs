using System.Globalization;
using System.Text;
using Meows.Plugins.Abstractions;

namespace Meows.Plugins.Basket.Services;

/// <summary>One tick box on a card. Small on purpose: a title and whether it is done.</summary>
public sealed class BasketCheckItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Title { get; set; } = "";

    public bool Done { get; set; }
}

/// <summary>
/// One card on the board, as it is stored.
///
/// A mutable class rather than a record, because this is what the settings file holds and it is
/// edited field by field on screen. Everything about it survives a restart, including the id,
/// which is what a row is found by after the board moved underneath it.
/// </summary>
public sealed class BasketCard
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Title { get; set; } = "";

    public string Notes { get; set; } = "";

    /// <summary>The day it is due. Null means whenever. Time of day is never part of it.</summary>
    public DateTime? Due { get; set; }

    public bool Done { get; set; }

    /// <summary>A file or folder this card points at. Optional, and never copied anywhere.</summary>
    public string? LinkPath { get; set; }

    public List<BasketCheckItem> Checklist { get; set; } = [];
}

/// <summary>One column on the board. Cards keep the order they were put in.</summary>
public sealed class BasketList
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Title { get; set; } = "";

    public List<BasketCard> Cards { get; set; } = [];
}

/// <summary>
/// The arithmetic behind the board: what is due, what the header says, and the markdown export.
///
/// Pure, and told what today is rather than reading the clock, so a test can sit on a date and a
/// pass that starts before midnight cannot answer differently to the one after it.
/// </summary>
public static class Board
{
    /// <summary>Anything due within this many days counts as coming up.</summary>
    public const int SoonDays = 7;

    /// <summary>A fresh board starts with somewhere to put things, somewhere they are, and somewhere they were.</summary>
    public static void EnsureDefaults(List<BasketList> lists)
    {
        if (lists.Count > 0)
            return;

        lists.Add(new BasketList { Title = "To do" });
        lists.Add(new BasketList { Title = "Doing" });
        lists.Add(new BasketList { Title = "Done" });
    }

    public static IEnumerable<(BasketList List, BasketCard Card)> AllCards(IEnumerable<BasketList> lists)
    {
        foreach (var list in lists)
            foreach (var card in list.Cards)
                yield return (list, card);
    }

    public static List<(BasketList List, BasketCard Card)> Overdue(IEnumerable<BasketList> lists, DateTime today) =>
        AllCards(lists)
            .Where(c => !c.Card.Done && c.Card.Due?.Date < today.Date)
            .OrderBy(c => c.Card.Due)
            .ToList();

    public static List<(BasketList List, BasketCard Card)> DueSoon(IEnumerable<BasketList> lists, DateTime today) =>
        AllCards(lists)
            .Where(c => !c.Card.Done && c.Card.Due?.Date >= today.Date && c.Card.Due?.Date <= today.Date.AddDays(SoonDays))
            .OrderBy(c => c.Card.Due)
            .ToList();

    /// <summary>The line under the header: what is late, what is coming up, and nothing else.</summary>
    public static string SummaryOf(IReadOnlyList<BasketList> lists, DateTime today, IMeowsText text)
    {
        var total = AllCards(lists).Count();
        if (total == 0)
            return "";

        var overdue = Overdue(lists, today).Count;
        var soon = DueSoon(lists, today).Count;

        if (overdue > 0 && soon > 0)
            return text.Format("basket.summary.both", overdue, soon);
        if (overdue > 0)
            return text.Format("basket.summary.overdue", overdue);
        if (soon > 0)
            return text.Format("basket.summary.soon", soon);

        return text.Format("basket.summary.clear", total);
    }

    /// <summary>The Home line from the board alone: red while something is late, nothing when there is nothing.</summary>
    public static Glance? GlanceOf(IReadOnlyList<BasketList> lists, DateTime today, IMeowsText text)
    {
        var summary = SummaryOf(lists, today, text);
        return summary.Length == 0
            ? null
            : new Glance(summary, Overdue(lists, today).Count > 0);
    }

    /// <summary>How one card reads: today, tomorrow, in so many days, so many days ago, or finished.</summary>
    public static string DueText(BasketCard card, DateTime today, IMeowsText text, CultureInfo culture)
    {
        if (card.Done)
            return text["basket.due.done"];
        if (card.Due is not { } due)
            return text["basket.due.none"];

        var date = due.ToString("d", culture);
        var days = (int)(due.Date - today.Date).TotalDays;

        return days switch
        {
            0 => text.Format("basket.due.today", date),
            1 => text.Format("basket.due.tomorrow", date),
            < 0 => text.Format("basket.due.late", date, -days),
            _ => text.Format("basket.due.left", date, days),
        };
    }

    /// <summary>The board as markdown, for sending to someone without Meows. One file, no surprises.</summary>
    public static string ExportMarkdown(IReadOnlyList<BasketList> lists, DateTime today)
    {
        var out_ = new StringBuilder();
        out_.AppendLine($"# Basket — {today:yyyy-MM-dd}");
        out_.AppendLine();

        foreach (var list in lists)
        {
            out_.AppendLine($"## {list.Title}");
            out_.AppendLine();

            if (list.Cards.Count == 0)
            {
                out_.AppendLine("_Empty._");
                out_.AppendLine();
                continue;
            }

            foreach (var card in list.Cards)
            {
                var box = card.Done ? "[x]" : "[ ]";
                var title = card.Title.Trim().Length > 0 ? card.Title.Trim() : "Untitled";
                var due = card.Due?.ToString("yyyy-MM-dd") ?? "no due date";
                out_.AppendLine($"- {box} {title} (due {due})");

                if (!string.IsNullOrWhiteSpace(card.Notes))
                    foreach (var line in card.Notes.Split('\n'))
                        out_.AppendLine($"  - {line.Trim()}");

                foreach (var check in card.Checklist)
                    out_.AppendLine($"  - [{(check.Done ? "x" : " ")}] {check.Title}");
            }

            out_.AppendLine();
        }

        return out_.ToString();
    }

    public static (BasketList List, BasketCard Card)? FindCard(IEnumerable<BasketList> lists, string id)
    {
        foreach (var (list, card) in AllCards(lists))
            if (card.Id == id)
                return (list, card);
        return null;
    }

    /// <summary>
    /// A file or folder name as a card title: separators become spaces, and a leading date is
    /// dropped because the due date already says when. A guess, so adding a card costs a drag
    /// rather than filling in a form.
    /// </summary>
    public static string TitleFromPath(string path)
    {
        // Split on both separators rather than asking the platform, so a board written on Windows
        // and read anywhere else still titles its cards the same way.
        var lastSlash = path.LastIndexOfAny(['/', '\\']);
        var stripped = (lastSlash >= 0 ? path[(lastSlash + 1)..] : path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = stripped;
        var dot = name.LastIndexOf('.');
        if (dot > 0)
            name = name[..dot];

        var spaced = new StringBuilder();
        foreach (var c in name)
            spaced.Append(c is '_' or '-' or '.' ? ' ' : c);

        var words = spaced.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        while (words.Count > 1 && words[0].All(char.IsDigit) && words[0].Length is 4 or 6 or 8)
            words.RemoveAt(0);

        var title = string.Join(" ", words.Select(Cased));
        return title.Length switch
        {
            0 => path,
            > 80 => title[..80],
            _ => title,
        };
    }

    /// <summary>
    /// A file name is usually all lower case and a title is not. A word that already has a
    /// capital in it is left alone, so an acronym keeps its shape.
    /// </summary>
    private static string Cased(string word) =>
        word.Length > 0 && !word.Any(char.IsUpper)
            ? char.ToUpperInvariant(word[0]) + word[1..]
            : word;
}
