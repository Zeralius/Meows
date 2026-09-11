namespace Meows.Plugins.Tin.Services;

/// <summary>
/// Which column holds what. -1 means nobody knows yet.
///
/// The reference is optional in a way the other three are not: without it a row still reads, it
/// just says less. So it is guessed and never asked about, and a wrong guess there is cosmetic
/// rather than the difference between a readable file and an unreadable one.
/// </summary>
public sealed record ColumnMap(int Date, int Name, int Amount, int Reference = -1)
{
    public static ColumnMap None { get; } = new(-1, -1, -1);

    /// <summary>All three found. Anything less and the file cannot be read into charges.</summary>
    public bool IsComplete => Date >= 0 && Name >= 0 && Amount >= 0;
}

/// <summary>
/// Works out which column is which from the header line.
///
/// Every bank writes a different CSV, which is the one thing this plugin can be sure of. The
/// guess is a starting point and the tab lets it be corrected, because a wrong guess that cannot
/// be overruled is worse than no guess at all.
/// </summary>
public static class Columns
{
    // Ordered: the earlier a word appears, the more it looks like the column we want. "Betrag"
    // beats "Wert" for the amount, and who the money went to beats what the transfer said.
    private static readonly string[] DateWords =
    [
        "buchungstag", "buchungsdatum", "buchung", "wertstellung", "valuta", "datum",
        "booking date", "transaction date", "value date", "date", "posted", "completed",
    ];

    private static readonly string[] NameWords =
    [
        "beguenstigter", "begünstigter", "zahlungsempfänger", "zahlungspflichtiger", "empfänger",
        "auftraggeber", "name", "payee", "merchant", "counterparty", "partner", "beneficiary",
        "verwendungszweck", "buchungstext", "description", "reference", "details", "memo",
        "beschreibung", "subject", "narrative",
    ];

    /// <summary>
    /// What the payment was for, as opposed to who took it. These also appear in the payee list
    /// further down it, on purpose: when an export has only one such column it is the best name
    /// available, and when it has both, the payee wins the name and this wins the reference.
    /// </summary>
    private static readonly string[] PurposeWords =
    [
        "verwendungszweck", "buchungstext", "beschreibung", "description", "reference", "details",
        "memo", "subject", "narrative", "info",
    ];

    private static readonly string[] AmountWords =
    [
        "betrag", "umsatz", "amount", "value", "wert", "soll", "haben", "debit", "credit",
        "net", "gross", "sum",
    ];

    /// <summary>
    /// How much a row reads like a header. Used to find the header among the preamble lines, so
    /// it only has to be better at the job than the account number two lines above it.
    /// </summary>
    public static int Score(IReadOnlyList<string> row)
    {
        var score = 0;
        var kinds = new HashSet<string>();

        foreach (var cell in row)
        {
            if (Rank(cell, DateWords) >= 0)
                kinds.Add("date");
            if (Rank(cell, AmountWords) >= 0)
                kinds.Add("amount");
            if (Rank(cell, NameWords) >= 0)
                kinds.Add("name");
        }

        score += kinds.Count * 2;

        // A header cell is a word, not a number and not a sentence. A row of dates and amounts
        // can otherwise score on the odd word inside it.
        if (row.Count >= 3 && row.All(c => c.Trim().Length < 60))
            score += 1;

        return kinds.Count >= 2 ? score : 0;
    }

    public static ColumnMap Guess(IReadOnlyList<string> header)
    {
        var name = Best(header, NameWords);
        var reference = Best(header, PurposeWords);

        // One column cannot be both. When the export has a single text column, it is the name.
        return new ColumnMap(
            Best(header, DateWords),
            name,
            Best(header, AmountWords),
            reference == name ? -1 : reference);
    }

    /// <summary>
    /// The column whose heading matches the strongest word. Ties go to the leftmost, which is
    /// how a "Buchungstag" beats the "Wertstellung" beside it.
    ///
    /// A heading that *is* the word beats one that merely contains it. A Sparkasse export has a
    /// "Lastschrift Ursprungsbetrag" column standing eight columns to the left of "Betrag", and
    /// it is empty on every row that is not a returned direct debit: without this the whole file
    /// reads as unparseable, which is exactly how it was found.
    /// </summary>
    private static int Best(IReadOnlyList<string> header, string[] words)
    {
        var found = -1;
        var bestRank = int.MaxValue;

        for (var i = 0; i < header.Count; i++)
        {
            var rank = Rank(header[i], words);
            if (rank < 0 || rank >= bestRank)
                continue;

            bestRank = rank;
            found = i;
        }

        return found;
    }

    private static int Rank(string? cell, string[] words)
    {
        if (string.IsNullOrWhiteSpace(cell))
            return -1;

        var text = cell.Trim().ToLowerInvariant();

        for (var i = 0; i < words.Length; i++)
        {
            if (!text.Contains(words[i], StringComparison.Ordinal))
                continue;

            // Word order first, exactness only as the tiebreak. The other way round, a
            // "Verwendungszweck" that matches its word exactly would beat the
            // "Beguenstigter/Zahlungspflichtiger" beside it, and the payee is the one that
            // repeats.
            return text == words[i] ? i * 2 : i * 2 + 1;
        }

        return -1;
    }
}
