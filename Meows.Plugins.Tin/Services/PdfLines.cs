using UglyToad.PdfPig;

namespace Meows.Plugins.Tin.Services;

/// <summary>One word, and where across the page it sits.</summary>
public sealed record PdfWord(string Text, double Left, double Right);

/// <summary>
/// A line of a PDF, rebuilt from the words that share a baseline.
///
/// A PDF has no lines and no columns. It has glyphs at coordinates, and everything a person reads
/// as a table is an arrangement the eye does for itself. Putting the words back into rows is the
/// whole first half of reading a statement.
/// </summary>
public sealed record PdfLine(int Page, double Top, IReadOnlyList<PdfWord> Words)
{
    public string Text => string.Join(" ", Words.Select(w => w.Text));
}

public static class PdfLines
{
    /// <summary>
    /// Words within this fraction of the line height of each other are on the same line. Half a
    /// line is forgiving enough for the slight baseline differences between a bold heading and
    /// the text beside it, and tight enough not to swallow the row above.
    /// </summary>
    private const double Share = 0.5;

    public static IReadOnlyList<PdfLine> Read(string path)
    {
        using var document = PdfDocument.Open(path);

        var lines = new List<PdfLine>();

        foreach (var page in document.GetPages())
        {
            var words = page.GetWords()
                .Where(w => !string.IsNullOrWhiteSpace(w.Text))
                .Select(w => new
                {
                    w.Text,
                    Left = w.BoundingBox.Left,
                    Right = w.BoundingBox.Right,
                    Middle = (w.BoundingBox.Bottom + w.BoundingBox.Top) / 2,
                    Height = Math.Abs(w.BoundingBox.Height),
                })
                .OrderByDescending(w => w.Middle)
                .ToList();

            if (words.Count == 0)
                continue;

            var heights = words.Select(w => w.Height).Where(h => h > 0).OrderBy(h => h).ToList();
            var tolerance = heights.Count > 0 ? Math.Max(1.5, heights[heights.Count / 2] * Share) : 3;

            var current = new List<PdfWord>();
            var middle = words[0].Middle;

            foreach (var word in words)
            {
                if (current.Count > 0 && Math.Abs(word.Middle - middle) > tolerance)
                {
                    lines.Add(Line(page.Number, middle, current));
                    current = [];
                }

                if (current.Count == 0)
                    middle = word.Middle;

                current.Add(new PdfWord(word.Text.Trim(), word.Left, word.Right));
            }

            if (current.Count > 0)
                lines.Add(Line(page.Number, middle, current));
        }

        return lines;
    }

    /// <summary>
    /// Left to right, whatever order the writer laid the glyphs down in. A PDF is under no
    /// obligation to write a line in reading order, and plenty do not.
    /// </summary>
    private static PdfLine Line(int page, double middle, List<PdfWord> words) =>
        new(page, middle, words.OrderBy(w => w.Left).ToList());
}
