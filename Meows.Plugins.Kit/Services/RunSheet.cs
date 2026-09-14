using System.Text.RegularExpressions;

namespace Meows.Plugins.Kit.Services;

/// <summary>One line of a roster, worked out: how many of which token, or a line that is just a line.</summary>
public sealed record RosterLine(int Count, string Name, KitItem? Token)
{
    public bool IsGroup => Token is not null;
}

/// <summary>
/// The run sheet's arithmetic: a roster typed as lines becomes groups of tokens the VTT can
/// place. Lenient on purpose, because it is typed at a kitchen table: <c>3 Goblin</c>,
/// <c>3x Goblin</c>, <c>Goblin x3</c>, <c>Goblin ×3</c> and <c>Goblin (3)</c> all mean three
/// goblins, and <c>Goblin</c> alone means one. Names match tokens case-insensitively, by the
/// token's name first and its file's stem second.
/// </summary>
public static partial class RunSheet
{
    [GeneratedRegex(@"^\s*(\d+)\s*[x×]?\s+(.+?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex CountFirst();

    [GeneratedRegex(@"^\s*(.+?)\s*(?:[x×]\s*(\d+)|\(\s*(\d+)\s*\))\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex CountLast();

    public static IReadOnlyList<RosterLine> Parse(string roster, IReadOnlyList<KitItem> tokens)
    {
        var lines = new List<RosterLine>();
        foreach (var raw in roster.Split('\n'))
        {
            var line = raw.Trim().TrimStart('-', '*', '•').Trim();
            if (line.Length == 0)
                continue;

            int count;
            string name;
            if (CountFirst().Match(line) is { Success: true } first)
            {
                count = int.Parse(first.Groups[1].Value);
                name = first.Groups[2].Value;
            }
            else if (CountLast().Match(line) is { Success: true } last)
            {
                name = last.Groups[1].Value;
                count = int.Parse(last.Groups[2].Success ? last.Groups[2].Value : last.Groups[3].Value);
            }
            else
            {
                count = 1;
                name = line;
            }

            var token = Find(name, tokens);
            lines.Add(new RosterLine(Math.Max(1, count), token?.Name ?? name, token));
        }
        return lines;
    }

    private static KitItem? Find(string name, IReadOnlyList<KitItem> tokens)
    {
        var wanted = name.Trim();
        return tokens.FirstOrDefault(t => string.Equals(t.Name, wanted, StringComparison.OrdinalIgnoreCase))
            ?? tokens.FirstOrDefault(t => string.Equals(Path.GetFileNameWithoutExtension(t.File), wanted, StringComparison.OrdinalIgnoreCase))
            // "Goblins" for a token called Goblin, and the other way round.
            ?? tokens.FirstOrDefault(t => string.Equals(t.Name + "s", wanted, StringComparison.OrdinalIgnoreCase)
                                          || string.Equals(t.Name, wanted + "s", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"3 × Goblin, 1 × Bugbear, and one line" for a card.</summary>
    public static string Summarise(IReadOnlyList<RosterLine> lines)
    {
        var groups = lines.Where(l => l.IsGroup).Select(l => l.Count == 1 ? l.Name : $"{l.Count} × {l.Name}").ToList();
        return string.Join(", ", groups);
    }
}
