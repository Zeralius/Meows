using System.Globalization;
using System.Text.RegularExpressions;

namespace Meows.Plugins.Tin.Services;

/// <summary>What came out of one PDF.</summary>
public sealed record PdfReading(IReadOnlyList<Charge> Charges, int Lines, string? Problem)
{
    public static PdfReading Nothing { get; } = new([], 0, PdfStatement.ProblemNothing);
}

/// <summary>
/// Reads a statement out of a PDF.
///
/// This exists because some banks hand out PDFs and nothing else. It is the awkward path and
/// always will be: a CSV says what its columns mean, and a PDF says where the ink went. What
/// follows is the arithmetic that turns the second back into the first, and every rule in it is
/// a rule about how statements are laid out rather than about what they contain.
/// </summary>
public static class PdfStatement
{
    public const string ProblemNothing = "tin.problem.pdfnothing";
    public const string ProblemSigns = "tin.problem.pdfsigns";

    /// <summary>Amount columns further apart than this are different columns.</summary>
    private const double ColumnGap = 12;

    /// <summary>
    /// A number written as money: thousands separated or not, but always two decimals. Without
    /// the two decimals a reference number, an invoice number and a year all read as amounts.
    /// </summary>
    private static readonly Regex AmountShape =
        new(@"^[-+(]?\d{1,3}(?:[.,]\d{3})*[.,]\d{2}\)?$", RegexOptions.Compiled);

    private static readonly Regex WithYear =
        new(@"^(\d{1,2})\.(\d{1,2})\.(\d{4})$", RegexOptions.Compiled);

    private static readonly Regex WithShortYear =
        new(@"^(\d{1,2})\.(\d{1,2})\.(\d{2})$", RegexOptions.Compiled);

    /// <summary>German statements print the booking day without a year, all year long.</summary>
    private static readonly Regex WithoutYear =
        new(@"^(\d{1,2})\.(\d{1,2})\.?$", RegexOptions.Compiled);

    private static readonly Regex AnyYear = new(@"\b(19|20)\d{2}\b", RegexOptions.Compiled);

    /// <summary>The stamp a card payment carries in its own details: 2026-09-09T16:26.</summary>
    private static readonly Regex Iso = new(@"^\d{4}-\d{2}-\d{2}", RegexOptions.Compiled);

    /// <summary>
    /// Lines that are the bank talking about the account rather than about a payment. Left in,
    /// the opening and closing balance become the two largest subscriptions you have.
    /// </summary>
    private static readonly string[] NotAPayment =
    [
        "SALDO", "KONTOSTAND", "UEBERTRAG", "ÜBERTRAG", "UBERTRAG", "ZWISCHENSUMME", "SUMME",
        "BALANCE", "CARRIED FORWARD", "BROUGHT FORWARD", "TOTAL", "ALTER", "NEUER",
    ];

    public static PdfReading Read(string path, DateTime fallback)
    {
        IReadOnlyList<PdfLine> lines;

        try
        {
            lines = PdfLines.Read(path);
        }
        catch (Exception)
        {
            // An encrypted PDF, or something that is not a PDF at all.
            return PdfReading.Nothing;
        }

        return Parse(lines, Path.GetFileName(path), fallback);
    }

    /// <summary>
    /// Reads a statement, whichever of the two shapes it is in.
    ///
    /// There are two, and they have nothing in common. A printed Kontoauszug is a table: one row
    /// per payment, starting with a date. An online banking "Umsätze" print is not a table at all:
    /// each payment is three stacked lines, the payee, then the amount alone out to the right,
    /// then the details, with the date carried by a heading above a whole group of them.
    ///
    /// Rather than sniff for which one it is, both are read and the one that understood more of
    /// the file wins. A layout guess that is only mostly right is the thing most likely to be
    /// wrong here, and counting what each actually produced is a better judge than any rule about
    /// how the pages look.
    /// </summary>
    public static PdfReading Parse(IReadOnlyList<PdfLine> lines, string source, DateTime fallback)
    {
        if (lines.Count == 0)
            return PdfReading.Nothing;

        var table = AsTable(lines, source, fallback);
        var stacked = AsBlocks(lines, source, fallback);

        if (stacked.Charges.Count > table.Charges.Count)
            return stacked;

        return table.Charges.Count > 0 ? table : Worse(table, stacked);
    }

    /// <summary>
    /// Neither shape produced anything, so the complaint should be the more specific of the two.
    /// "Rows found but no direction on them" tells the user something; "nothing found" does not.
    /// </summary>
    private static PdfReading Worse(PdfReading table, PdfReading stacked) =>
        table.Problem == ProblemSigns || stacked.Problem != ProblemSigns ? table : stacked;

    private static PdfReading AsTable(IReadOnlyList<PdfLine> lines, string source, DateTime fallback)
    {
        var rows = Rows(lines);
        if (rows.Count == 0)
            return new PdfReading([], lines.Count, ProblemNothing);

        var (debit, credit, single) = Columns(rows);

        var reference = Reference(lines, fallback);
        var charges = new List<Charge>();
        var unsigned = 0;

        foreach (var row in rows)
        {
            var picked = Pick(row, debit, credit, single);
            if (picked is not { } found)
                continue;

            var sign = Sign(row, found, debit, credit);
            if (sign == 0)
            {
                unsigned++;
                continue;
            }

            var date = Settle(row.Day, row.Month, row.Year, reference);
            var name = Statement.Tidy(string.Join(" ", row.Name));

            if (name.Length == 0 || IsNotAPayment(name))
                continue;

            charges.Add(new Charge(date, name, sign * Math.Abs(found.Value), source));
        }

        if (charges.Count == 0)
        {
            // Rows were found and none of them could be called money in or money out. Guessing
            // here would turn a salary into the largest subscription in the list.
            return new PdfReading([], lines.Count, unsigned > 0 ? ProblemSigns : ProblemNothing);
        }

        return new PdfReading(charges, lines.Count, null);
    }

    // ---- the stacked shape ----

    /// <summary>
    /// The layout an online banking "Umsätze" print uses, which is not a table.
    ///
    /// Each payment is three stacked lines: who it was, then the amount by itself out at the right
    /// margin, then the details underneath. The date is on none of them; it is a heading over a
    /// whole group of payments, off to the left of where the payments start.
    ///
    /// So the amount line is the anchor. The payee is the line above it, the details are the lines
    /// below it up to the next payment, and the date is whichever heading was passed last.
    /// </summary>
    private static PdfReading AsBlocks(IReadOnlyList<PdfLine> lines, string source, DateTime fallback)
    {
        // Where the amounts are printed. Everything hangs off this: an amount in that column is a
        // payment, and a number anywhere else on the page is a reference, a contract number or the
        // account balance in the heading.
        if (AmountColumn(lines) is not { } column)
            return new PdfReading([], lines.Count, ProblemNothing);

        var anchors = new List<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (IsAnchor(lines[i], column))
                anchors.Add(i);
        }

        // Three lines a payment, so a statement in this shape has a great many of these. One or
        // two mean a total at the foot of a table, which the other reading handles better.
        if (anchors.Count < 3)
            return new PdfReading([], lines.Count, ProblemNothing);

        // A minus somewhere in the file means this bank writes its outgoings with one, so an
        // amount without it is money coming in. Inferring that from nothing would be guessing;
        // inferring it from the file own habit is reading.
        var minusUsed = lines.Any(l => l.Words.Any(w => w.Text.StartsWith('-') && Amount(w.Text) is not null));

        var reference = Reference(lines, fallback);
        var charges = new List<Charge>();
        var unsigned = 0;

        DateTime? heading = null;
        DateTime? last = null;
        var next = 0;

        for (var i = 0; i < lines.Count; i++)
        {
            if (Heading(lines[i]) is { } day)
            {
                heading = day;
                continue;
            }

            if (next >= anchors.Count || anchors[next] != i)
                continue;

            var line = lines[i];
            next++;

            if (Anchored(line, column) is not { } amount)
                continue;

            var sign = amount.Marker switch
            {
                'S' or '-' => -1,
                'H' or '+' => 1,
                _ => amount.Value < 0 ? -1 : minusUsed ? 1 : 0,
            };

            if (sign == 0)
            {
                unsigned++;
                continue;
            }

            var name = Statement.Tidy(Payee(lines, i, column));
            if (name.Length == 0 || IsNotAPayment(name))
                continue;

            // What the payment says about itself comes first: a card payment stamps the moment
            // into its own details, and a direct debit prints the period it covers. Only then the
            // heading, because a statement may carry five of those in forty pages and one stale
            // heading silently dates half a year of payments to the same day. Failing both, the
            // payment keeps company with the one before it, these files running in date order.
            var until = next < anchors.Count ? anchors[next] : lines.Count;

            var date = Stamped(lines, i, until, reference)
                       ?? heading
                       ?? last
                       ?? reference;

            last = date;
            charges.Add(new Charge(date, name, sign * Math.Abs(amount.Value), source,
                Details(lines, i, until, next < anchors.Count, column)));
        }

        if (charges.Count == 0)
            return new PdfReading([], lines.Count, unsigned > 0 ? ProblemSigns : ProblemNothing);

        return new PdfReading(charges, lines.Count, null);
    }

    /// <summary>
    /// The right edge that most of the amounts share.
    ///
    /// The right edge rather than the left, because money is printed right aligned: 9,00 and
    /// 1.755,99 in the same column start in quite different places and end in the same one.
    /// </summary>
    private static double? AmountColumn(IReadOnlyList<PdfLine> lines)
    {
        var edges = lines
            .SelectMany(l => l.Words)
            .Where(w => Amount(w.Text) is not null)
            .Select(w => w.Right)
            .OrderBy(edge => edge)
            .ToList();

        if (edges.Count == 0)
            return null;

        // Grouped by how far apart they are rather than by rounding to a grid, because two edges
        // a millimetre apart can still land either side of a grid line. A column is a run of
        // edges with no real gap in it.
        var best = new List<double>();
        var current = new List<double> { edges[0] };

        foreach (var edge in edges.Skip(1))
        {
            if (edge - current[^1] > ColumnGap)
            {
                if (current.Count > best.Count)
                    best = current;

                current = [];
            }

            current.Add(edge);
        }

        if (current.Count > best.Count)
            best = current;

        // One or two numbers in a column is a total at the foot of something, not a statement.
        return best.Count < 3 ? null : best[best.Count / 2];
    }

    /// <summary>
    /// The one amount printed in the money column, if this line has exactly one.
    ///
    /// It does not have to be alone on the line. Most of them are, but a long payment reference
    /// sometimes shares a baseline with the amount belonging to it, and requiring the line be
    /// nothing but a number quietly dropped one payment in four.
    /// </summary>
    private static Found? Anchored(PdfLine line, double column)
    {
        Found? found = null;

        foreach (var word in line.Words)
        {
            if (Amount(word.Text) is not { } amount || Math.Abs(word.Right - column) > ColumnGap)
                continue;

            // Two amounts in the money column is not a payment line; it is a table this reading
            // does not understand, and the other one may.
            if (found is not null)
                return null;

            found = amount;
        }

        return found;
    }

    private static bool IsAnchor(PdfLine line, double column) => Anchored(line, column) is not null;

    /// <summary>A line that is one date and nothing else: the heading over a day of payments.</summary>
    private static DateTime? Heading(PdfLine line)
    {
        if (line.Words.Count != 1)
            return null;

        if (Date(line.Words[0].Text) is not { } date || date.Year is null)
            return null;

        return Settle(date.Day, date.Month, date.Year, DateTime.Today);
    }

    /// <summary>
    /// What the payment said about itself: the lines under the amount, up to the next payment.
    ///
    /// The last of those lines belongs to the payment below rather than to this one, since that is
    /// where its payee sits, so it is left out. What remains is the customer number, the contract,
    /// the period, the terminal stamp — the half of a statement row that says which of a payee's
    /// three bills this one is.
    /// </summary>
    private static string Details(IReadOnlyList<PdfLine> lines, int from, int until, bool another, double column)
    {
        var last = another ? until - 1 : until;
        var text = new List<string>();

        for (var i = from + 1; i < last && i < lines.Count; i++)
        {
            if (IsAnchor(lines[i], column) || Heading(lines[i]) is not null)
                continue;

            text.Add(lines[i].Text);
        }

        var joined = Statement.Tidy(string.Join(" ", text));
        return joined.Length > 200 ? joined[..200] : joined;
    }

    /// <summary>Who it was: the nearest line above that is not a heading or another amount.</summary>
    private static string Payee(IReadOnlyList<PdfLine> lines, int at, double column)
    {
        for (var i = at - 1; i >= 0 && i >= at - 3; i--)
        {
            if (IsAnchor(lines[i], column) || Heading(lines[i]) is not null)
                continue;

            return lines[i].Text;
        }

        return "";
    }

    /// <summary>
    /// The date a card payment stamps into its own details as an ISO timestamp, or failing that
    /// any dated day printed under the payment.
    /// </summary>
    private static DateTime? Stamped(IReadOnlyList<PdfLine> lines, int from, int until, DateTime reference)
    {
        for (var i = from + 1; i < until && i < lines.Count; i++)
        {
            foreach (var word in lines[i].Words)
            {
                var iso = Iso.Match(word.Text);
                if (iso.Success && DateTime.TryParse(iso.Value, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var stamped) && Plausible(stamped.Date, reference))
                    return stamped.Date;
            }
        }

        for (var i = from + 1; i < until && i < lines.Count; i++)
        {
            foreach (var word in lines[i].Words)
            {
                if (Date(word.Text) is not { Year: not null } date)
                    continue;

                var settled = Settle(date.Day, date.Month, date.Year, reference);
                if (Plausible(settled, reference))
                    return settled;
            }
        }

        return null;
    }

    /// <summary>
    /// Close enough to the statement to be one of its payments.
    ///
    /// Details lines are full of dates that are not the booking date: a contract period, a due
    /// date next year, a membership paid up until some date, and one export truncates a year to
    /// "01.10.20" which reads perfectly well as 2020. A statement covers months, not decades.
    /// </summary>
    private static bool Plausible(DateTime date, DateTime reference) =>
        Math.Abs((date - reference).TotalDays) <= 550;

    // ---- rows ----

    private readonly record struct Found(double Left, decimal Value, char Marker, bool Explicit);

    private sealed class Row
    {
        public int Day;
        public int Month;
        public int? Year;
        public List<string> Name = [];
        public List<Found> Amounts = [];
    }

    /// <summary>
    /// Every line that begins with a date starts a row. Lines under it that begin with neither a
    /// date nor an amount belong to the row above, because that is how a long payment reference
    /// is printed.
    /// </summary>
    private static List<Row> Rows(IReadOnlyList<PdfLine> lines)
    {
        var rows = new List<Row>();

        foreach (var line in lines)
        {
            var words = line.Words;
            var at = 0;

            var day = 0;
            var month = 0;
            int? year = null;
            var dated = false;

            // Booking date and value date sit side by side on most statements. The first is the
            // one that matters and the second is noise.
            while (at < words.Count && Date(words[at].Text) is { } date)
            {
                if (!dated)
                {
                    (day, month, year) = date;
                    dated = true;
                }

                at++;
            }

            var amounts = new List<Found>();
            var name = new List<string>();

            for (var i = at; i < words.Count; i++)
            {
                var text = words[i].Text;

                if (Amount(text) is { } value)
                {
                    // Read before the marker is swallowed: the column is where the number sits,
                    // not where the S after it sits.
                    var left = words[i].Left;
                    var marker = '\0';

                    // A marker sitting on its own after the number: 29,90 S
                    if (i + 1 < words.Count && Marker(words[i + 1].Text) is { } next)
                    {
                        marker = next;
                        i++;
                    }

                    amounts.Add(value with
                    {
                        Left = left,
                        Marker = value.Marker == '\0' ? marker : value.Marker,
                    });
                    continue;
                }

                name.Add(text);
            }

            if (dated)
            {
                rows.Add(new Row { Day = day, Month = month, Year = year, Name = name, Amounts = amounts });
                continue;
            }

            // A continuation of the row above: more of the same payment reference, wrapped.
            if (rows.Count > 0 && amounts.Count == 0 && name.Count > 0 && rows[^1].Name.Count < 12)
                rows[^1].Name.AddRange(name);
        }

        return rows.Where(r => r.Amounts.Count > 0).ToList();
    }

    // ---- columns ----

    /// <summary>
    /// Which of the amount columns is money out, which is money in, and which is the running
    /// balance nobody asked about.
    ///
    /// Told apart by how often they appear together. A debit and credit pair holds one amount per
    /// row and never both; an amount and a balance hold two on nearly every row. Getting this
    /// wrong reads the balance as the payment, which is the one mistake here that produces
    /// numbers that look entirely plausible.
    /// </summary>
    private static (double? Debit, double? Credit, double? Single) Columns(List<Row> rows)
    {
        var clusters = Cluster(rows.SelectMany(r => r.Amounts).Select(a => a.Left));

        switch (clusters.Count)
        {
            case 0:
                return (null, null, null);
            case 1:
                return (null, null, clusters[0]);
        }

        var both = rows.Count(r =>
            r.Amounts.Any(a => Near(a.Left, clusters[0])) && r.Amounts.Any(a => Near(a.Left, clusters[1])));

        if (clusters.Count == 2 && both * 2 >= rows.Count)
        {
            // Amount then balance. The left one is the payment.
            return (null, null, clusters[0]);
        }

        return (clusters[0], clusters[1], null);
    }

    private static List<double> Cluster(IEnumerable<double> values)
    {
        var found = new List<double>();

        foreach (var value in values.OrderBy(v => v))
        {
            if (found.Count == 0 || value - found[^1] > ColumnGap)
                found.Add(value);
        }

        return found;
    }

    private static bool Near(double value, double? column) =>
        column is { } c && Math.Abs(value - c) <= ColumnGap;

    private static Found? Pick(Row row, double? debit, double? credit, double? single)
    {
        if (single is not null)
        {
            foreach (var amount in row.Amounts)
            {
                if (Near(amount.Left, single))
                    return amount;
            }

            return null;
        }

        foreach (var amount in row.Amounts)
        {
            if (Near(amount.Left, debit) || Near(amount.Left, credit))
                return amount;
        }

        return null;
    }

    /// <summary>
    /// Money out is -1, money in is 1, and 0 means the statement never said.
    ///
    /// A marker beats a column, because a marker is the bank being explicit and a column is this
    /// code being clever.
    /// </summary>
    private static int Sign(Row row, Found found, double? debit, double? credit)
    {
        _ = row;

        if (found.Marker is 'S' or '-')
            return -1;

        if (found.Marker is 'H' or '+')
            return 1;

        if (found.Explicit)
            return found.Value < 0 ? -1 : 1;

        if (debit is not null && Near(found.Left, debit))
            return -1;

        if (credit is not null && Near(found.Left, credit))
            return 1;

        return 0;
    }

    // ---- the pieces of a line ----

    private static (int Day, int Month, int? Year)? Date(string text)
    {
        // A date at the end of a sentence keeps the sentence's punctuation: "DATUM 03.07.2026,".
        text = text.TrimEnd(',', ';', ':');

        var full = WithYear.Match(text);
        if (full.Success)
            return (int.Parse(full.Groups[1].Value), int.Parse(full.Groups[2].Value),
                int.Parse(full.Groups[3].Value));

        var shortened = WithShortYear.Match(text);
        if (shortened.Success)
            return (int.Parse(shortened.Groups[1].Value), int.Parse(shortened.Groups[2].Value),
                2000 + int.Parse(shortened.Groups[3].Value));

        var bare = WithoutYear.Match(text);
        if (!bare.Success)
            return null;

        var day = int.Parse(bare.Groups[1].Value);
        var month = int.Parse(bare.Groups[2].Value);

        return day is >= 1 and <= 31 && month is >= 1 and <= 12 ? (day, month, null) : null;
    }

    private static Found? Amount(string text)
    {
        var marker = '\0';
        var body = text;

        // 1.234,56S and 1.234,56- both happen, with no space in front of the marker.
        if (body.Length > 1 && Marker(body[^1].ToString()) is { } glued && !char.IsDigit(body[^1]))
        {
            marker = glued;
            body = body[..^1];
        }

        if (!AmountShape.IsMatch(body))
            return null;

        if (!Money.TryAmount(body, out var value))
            return null;

        var signed = body.StartsWith('-') || body.StartsWith('(') || marker is '-' or '+';

        return new Found(0, value, marker, signed);
    }

    private static char? Marker(string text) => text.Trim().ToUpperInvariant() switch
    {
        "S" => 'S',
        "H" => 'H',
        "-" => '-',
        "+" => '+',
        _ => null,
    };

    private static bool IsNotAPayment(string name)
    {
        var upper = name.ToUpperInvariant();
        return NotAPayment.Any(word => upper.StartsWith(word, StringComparison.Ordinal));
    }

    // ---- the missing year ----

    /// <summary>
    /// The date the statement is about, used to fill in the year German statements leave off.
    /// The latest full date printed anywhere on it, or failing that the year printed in the
    /// heading, or failing that when the file was written.
    /// </summary>
    public static DateTime Reference(IReadOnlyList<PdfLine> lines, DateTime fallback)
    {
        var latest = DateTime.MinValue;

        foreach (var word in lines.SelectMany(l => l.Words))
        {
            var match = WithYear.Match(word.Text);
            if (!match.Success)
                continue;

            if (DateTime.TryParseExact(word.Text, ["d.M.yyyy", "dd.MM.yyyy"], CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date) && date > latest)
                latest = date;
        }

        if (latest > DateTime.MinValue)
            return latest;

        foreach (var line in lines)
        {
            var year = AnyYear.Match(line.Text);
            if (year.Success && int.TryParse(year.Value, out var value) && value is >= 1990 and <= 2100)
                return new DateTime(value, 12, 31);
        }

        return fallback;
    }

    /// <summary>
    /// A day and a month, given a year.
    ///
    /// A statement printed in January carries December's bookings, so a date that lands well
    /// after the statement itself belongs to the year before. Six weeks of slack, because a
    /// statement is about the recent past and never about the future.
    /// </summary>
    public static DateTime Settle(int day, int month, int? year, DateTime reference)
    {
        if (year is { } known)
            return Safe(known, month, day);

        var guess = Safe(reference.Year, month, day);
        return guess > reference.Date.AddDays(45) ? Safe(reference.Year - 1, month, day) : guess;
    }

    /// <summary>The 31st of a month that has 30 days is a misread, not a reason to throw.</summary>
    private static DateTime Safe(int year, int month, int day)
    {
        year = Math.Clamp(year, 1, 9999);
        month = Math.Clamp(month, 1, 12);
        day = Math.Clamp(day, 1, DateTime.DaysInMonth(year, month));
        return new DateTime(year, month, day);
    }
}
