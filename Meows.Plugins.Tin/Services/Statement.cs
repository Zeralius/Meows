namespace Meows.Plugins.Tin.Services;

/// <summary>
/// One line of a bank export. Negative means it left the account.
///
/// The reference is what the payment said about itself beyond who took the money: the customer
/// number, the contract, the period it covers. It is never grouped on, because it is the part that
/// changes from month to month, but it is the part that says <em>which</em> of the three things
/// this payee bills you for a given row is.
/// </summary>
public sealed record Charge(DateTime Date, string Name, decimal Amount, string Source, string Reference = "");

/// <summary>One export file, and how well it was understood.</summary>
public sealed record SourceFile(
    string Path,
    string FileName,
    string Signature,
    IReadOnlyList<string> Header,
    ColumnMap Map,
    int Rows,
    int Charges,
    string? Problem,
    string Account = Iban.Unknown)
{
    public bool IsUnderstood => Problem is null;
}

/// <summary>What a folder of exports came to.</summary>
public sealed record Reading(IReadOnlyList<SourceFile> Sources, IReadOnlyList<Charge> Charges, int Duplicates)
{
    public static Reading Empty { get; } = new([], [], 0);

    /// <summary>
    /// The accounts these files are about, in the order they were first met. A folder is very
    /// often one account; it is just as often two, because the statements of a second account
    /// were downloaded into the same place.
    /// </summary>
    public IReadOnlyList<string> Accounts => Sources
        .Where(s => s.Charges > 0)
        .Select(s => s.Account)
        .Distinct(StringComparer.Ordinal)
        .ToList();

    /// <summary>Which account a charge belongs to, found through the file it came out of.</summary>
    public string AccountOf(Charge charge) => Sources
        .FirstOrDefault(s => string.Equals(s.FileName, charge.Source, StringComparison.OrdinalIgnoreCase))
        ?.Account ?? Iban.Unknown;
}

/// <summary>
/// Reads a folder of bank exports into one list of charges.
///
/// Read only, on purpose and permanently. These are the most private files this app will ever be
/// pointed at, so nothing here writes to the folder, moves anything, or keeps a copy: what Tin
/// remembers about them is a column mapping, and that lives with Meows' own settings.
/// </summary>
public static class Statement
{
    public const string ProblemUnreadable = "tin.problem.unreadable";
    public const string ProblemColumns = "tin.problem.columns";
    public const string ProblemNoRows = "tin.problem.norows";
    public const string ProblemNotData = "tin.problem.notdata";

    /// <summary>What is actually read.</summary>
    private static readonly HashSet<string> Readable =
        new(StringComparer.OrdinalIgnoreCase) { ".csv", ".txt", ".pdf" };

    /// <summary>
    /// Things that are plainly a statement and plainly not readable.
    ///
    /// Listed rather than ignored, because a folder holding one PDF and nothing else used to
    /// produce an empty tab and no explanation at all. A file that cannot be used has to say so
    /// where the files are listed; silence reads as a broken plugin.
    /// </summary>
    private static readonly HashSet<string> Refused =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".xls", ".xlsx", ".ods", ".ofx", ".qif", ".xml", ".mt940", ".sta", ".doc", ".docx",
        };

    public static Reading Read(string folder, IReadOnlyDictionary<string, ColumnMap>? saved = null,
        IReadOnlyDictionary<string, string>? accounts = null)
    {
        if (!Directory.Exists(folder))
            return Reading.Empty;

        string[] files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                .Where(f => Readable.Contains(Path.GetExtension(f)) || Refused.Contains(Path.GetExtension(f)))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception)
        {
            return Reading.Empty;
        }

        var sources = new List<SourceFile>();
        var charges = new List<Charge>();

        // Exports overlap. Asking for the last ninety days twice in a month means most of one
        // file is already in the other, and a subscription counted twice is a subscription that
        // looks like it doubled in price.
        var seen = new Ledger();
        var duplicates = 0;

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);

            if (!Readable.Contains(Path.GetExtension(file)))
            {
                sources.Add(new SourceFile(file, name, "", [], ColumnMap.None, 0, 0, ProblemNotData));
                continue;
            }

            if (Path.GetExtension(file).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                sources.Add(ReadPdf(file, name, Told(accounts, name), charges, seen, ref duplicates));
                continue;
            }

            CsvTable table;
            try
            {
                table = Csv.ReadFile(file);
            }
            catch (Exception)
            {
                sources.Add(new SourceFile(file, name, "", [], ColumnMap.None, 0, 0, ProblemUnreadable));
                continue;
            }

            if (table.Header.Count == 0 || table.Rows.Count == 0)
            {
                sources.Add(new SourceFile(file, name, table.Signature, table.Header, ColumnMap.None,
                    table.Rows.Count, 0, ProblemNoRows));
                continue;
            }

            // The account the file is about, which it names somewhere: a column of its own, the
            // heading, a line above the table. Whatever the user said wins over what was found.
            var account = Told(accounts, name)
                          ?? Iban.Find(table.Rows.Take(40).Select(r => string.Join(" ", r))
                              .Prepend(string.Join(" ", table.Header)))
                          ?? Iban.Unknown;

            var map = saved is not null && saved.TryGetValue(table.Signature, out var remembered)
                ? remembered
                : Columns.Guess(table.Header);

            if (!map.IsComplete)
            {
                sources.Add(new SourceFile(file, name, table.Signature, table.Header, map,
                    table.Rows.Count, 0, ProblemColumns, account));
                continue;
            }

            var taken = 0;
            foreach (var row in table.Rows)
            {
                if (map.Date >= row.Length || map.Name >= row.Length || map.Amount >= row.Length)
                    continue;

                if (!Money.TryDate(row[map.Date], out var date))
                    continue;

                if (!Money.TryAmount(row[map.Amount], out var amount) || amount == 0m)
                    continue;

                var payee = Tidy(row[map.Name]);
                if (payee.Length == 0)
                    continue;

                var reference = map.Reference >= 0 && map.Reference < row.Length
                    ? Tidy(row[map.Reference])
                    : "";

                if (!seen.Add(account, name, date, amount, payee))
                {
                    duplicates++;
                    continue;
                }

                charges.Add(new Charge(date, payee, amount, name, reference));
                taken++;
            }

            sources.Add(new SourceFile(file, name, table.Signature, table.Header, map,
                table.Rows.Count, taken, taken == 0 ? ProblemNoRows : null, account));
        }

        return new Reading(sources, charges, duplicates);
    }

    /// <summary>
    /// What has already been counted.
    ///
    /// Two rules, because the two kinds of duplicate are different. Inside one file, the same
    /// payee for the same amount on the same day is one payment written twice, which is what
    /// overlapping exports from one bank look like. Across two files, the dates rarely agree to
    /// the day: a booking date in one and a value date in the other, or a PDF statement beside the
    /// CSV of the same months, so the same payee for the same amount within a few days is the same
    /// payment seen twice.
    ///
    /// The looser rule is deliberately not used inside a file. Two identical payments in one
    /// export, a few days apart, are two real payments, and merging those would be inventing
    /// tidiness that is not there.
    ///
    /// Neither rule reaches across accounts. The same standing order leaving two accounts on the
    /// same day is two payments, and a folder holding statements for two accounts is the ordinary
    /// case rather than the strange one.
    /// </summary>
    private sealed class Ledger
    {
        /// <summary>How far two exports may disagree about the day of the same payment.</summary>
        private const int Slack = 3;

        private readonly HashSet<string> _exact = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<(DateTime Date, string Source)>> _loose = new(StringComparer.Ordinal);

        public bool Add(string account, string source, DateTime date, decimal amount, string payee)
        {
            var key = $"{account}|{amount}|{Recurring.KeyOf(payee)}";

            if (!_exact.Add($"{date:yyyy-MM-dd}|{source}|{key}"))
                return false;

            if (_loose.TryGetValue(key, out var already))
            {
                foreach (var (seen, from) in already)
                {
                    if (from != source && Math.Abs((seen - date).TotalDays) <= Slack)
                        return false;
                }
            }
            else
            {
                _loose[key] = already = [];
            }

            already.Add((date, source));
            return true;
        }
    }

    /// <summary>
    /// A statement the bank only ever published as a PDF.
    ///
    /// The awkward path, and the one with the most ways to be subtly wrong, so what it managed to
    /// read is reported beside how much was in the file. A PDF that gave up nothing says so rather
    /// than quietly contributing no charges.
    /// </summary>
    private static SourceFile ReadPdf(string file, string name, string? told, List<Charge> charges,
        Ledger seen, ref int duplicates)
    {
        PdfReading reading;
        IReadOnlyList<PdfLine> lines;

        try
        {
            lines = PdfLines.Read(file);
            reading = PdfStatement.Parse(lines, name, System.IO.File.GetLastWriteTime(file));
        }
        catch (Exception)
        {
            return new SourceFile(file, name, "", [], ColumnMap.None, 0, 0, ProblemUnreadable);
        }

        // The heading, where a statement prints the account it is about, before the rows start
        // naming everybody else's.
        var account = told ?? Iban.Find(lines.Take(40).Select(l => l.Text)) ?? Iban.Unknown;

        var taken = 0;

        foreach (var charge in reading.Charges)
        {
            if (!seen.Add(account, name, charge.Date, charge.Amount, charge.Name))
            {
                duplicates++;
                continue;
            }

            charges.Add(charge);
            taken++;
        }

        return new SourceFile(file, name, "", [], ColumnMap.None, reading.Lines, taken,
            reading.Problem ?? (taken == 0 ? ProblemNoRows : null), account);
    }

    /// <summary>What the user said this file belongs to, if they said anything.</summary>
    private static string? Told(IReadOnlyDictionary<string, string>? accounts, string fileName) =>
        accounts is not null && accounts.TryGetValue(fileName, out var told) && told.Length > 0
            ? told
            : null;

    /// <summary>
    /// A payee as a human would read it. Exports pad the field with spaces and newlines, and a
    /// name that differs only in whitespace is the same name.
    /// </summary>
    public static string Tidy(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var collapsed = new System.Text.StringBuilder();
        var space = false;

        foreach (var c in text.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space && collapsed.Length > 0)
                collapsed.Append(' ');

            space = false;
            collapsed.Append(c);
        }

        return collapsed.ToString();
    }
}
