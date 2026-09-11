namespace Meows.Plugins.Tin.Services;

/// <summary>A CSV read into a header row and the rows under it.</summary>
public sealed record CsvTable(char Delimiter, IReadOnlyList<string> Header, IReadOnlyList<string[]> Rows)
{
    public static CsvTable Empty { get; } = new(';', [], []);

    /// <summary>
    /// The header, lower cased and joined. Two exports from the same bank share it and an export
    /// from a different one does not, which makes it the key a saved column mapping hangs on.
    /// </summary>
    public string Signature => string.Join("|", Header.Select(h => h.Trim().ToLowerInvariant()));
}

/// <summary>
/// Reads a bank export. Not a general CSV library: it handles quoting, the three delimiters
/// exports actually use, and the preamble lines banks like to put above the real header.
/// </summary>
public static class Csv
{
    private static readonly char[] Candidates = [';', ',', '\t'];

    public static CsvTable Read(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return CsvTable.Empty;

        var delimiter = Delimiter(text);
        var rows = Split(text, delimiter);
        if (rows.Count == 0)
            return CsvTable.Empty;

        var header = HeaderRow(rows);
        if (header < 0)
            return new CsvTable(delimiter, [], []);

        var width = rows[header].Length;

        // Rows narrower than the header are the bank's own footer lines, an account total or a
        // blank. Rows wider than it are a quoting accident, and taking the first columns of one
        // is guessing; both are dropped rather than half read.
        var body = rows.Skip(header + 1)
            .Where(r => r.Length == width && r.Any(c => c.Trim().Length > 0))
            .ToList();

        return new CsvTable(delimiter, rows[header], body);
    }

    public static CsvTable ReadFile(string path) => Read(ReadAllText(path));

    /// <summary>
    /// Bank exports are Windows-1252 more often than they are UTF-8, and the difference shows up
    /// as a mangled umlaut in the middle of a payee name. A byte order mark settles it; without
    /// one, anything that is not valid UTF-8 is read as Windows-1252 instead.
    /// </summary>
    public static string ReadAllText(string path)
    {
        var bytes = File.ReadAllBytes(path);

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        try
        {
            return new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (ArgumentException)
        {
            return System.Text.Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>Whichever candidate appears most often outside quotes in the first few lines.</summary>
    public static char Delimiter(string text)
    {
        var sample = string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Take(10));
        var best = ';';
        var bestCount = 0;

        foreach (var candidate in Candidates)
        {
            var count = 0;
            var quoted = false;
            foreach (var c in sample)
            {
                if (c == '"')
                    quoted = !quoted;
                else if (!quoted && c == candidate)
                    count++;
            }

            if (count > bestCount)
            {
                bestCount = count;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// The header is the row that reads most like one, out of the first fifteen. Banks put an
    /// account number, an IBAN and a blank line above it, and taking the first row on faith gives
    /// a table whose only column is called "Kontonummer: DE12 …".
    /// </summary>
    private static int HeaderRow(IReadOnlyList<string[]> rows)
    {
        var best = -1;
        var bestScore = 0;

        for (var i = 0; i < Math.Min(15, rows.Count); i++)
        {
            var score = Columns.Score(rows[i]);
            if (score > bestScore)
            {
                bestScore = score;
                best = i;
            }
        }

        if (best >= 0)
            return best;

        // Nothing recognisable. The widest of the early rows is still a better guess than the
        // first one, and the user can point the columns at what they mean.
        for (var i = 0; i < Math.Min(15, rows.Count); i++)
        {
            if (rows[i].Length >= 3)
                return i;
        }

        return rows.Count > 0 ? 0 : -1;
    }

    private static List<string[]> Split(string text, char delimiter)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new System.Text.StringBuilder();
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (quoted)
            {
                if (c != '"')
                {
                    field.Append(c);
                }
                else if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                {
                    quoted = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row.ToArray());
                    row.Clear();
                    break;
                default:
                    if (c == delimiter)
                    {
                        row.Add(field.ToString());
                        field.Clear();
                    }
                    else
                    {
                        field.Append(c);
                    }

                    break;
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row.ToArray());
        }

        return rows.Where(r => r.Length > 0).ToList();
    }
}
