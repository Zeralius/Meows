using Meows.Plugins.Abstractions;

namespace Meows.Services;

/// <summary>
/// The week on one page, for the fridge door: what is up now, what the week held, and what
/// happened since the window was last hidden. Markdown, so anything prints it: an editor, a
/// browser, the phone. Read from what Home already shows; nothing new is looked up.
/// </summary>
public static class FamilyPage
{
    /// <summary>One card's lines with its name, in the order Home shows them.</summary>
    public sealed record CardLines(string Name, IReadOnlyList<Glance> Lines);

    /// <summary>Renders the page. Empty sections read as quiet weeks rather than vanishing.</summary>
    public static string Of(
        IReadOnlyList<CardLines> cards,
        IReadOnlyList<string> week,
        IReadOnlyList<(string Plugin, string Subject)> recent,
        DateTime today,
        IMeowsText text)
    {
        var lines = new List<string>
        {
            $"# {text["home.page.title"]} — {today:d}",
            "",
            $"## {text["home.page.upnow"]}",
            "",
        };

        var said = cards
            .Where(c => c.Lines.Count > 0 && c.Lines.Any(l => l.Text.Length > 0))
            .ToList();
        if (said.Count == 0)
            lines.Add(text["home.page.quiet"]);
        foreach (var card in said)
            foreach (var glance in card.Lines.Where(l => l.Text.Length > 0))
                lines.Add($"- **{card.Name}**: {glance.Text}");
        lines.Add("");

        lines.Add($"## {text["home.page.week"]}");
        lines.Add("");
        if (week.Count == 0)
            lines.Add(text["home.page.quiet"]);
        foreach (var line in week)
            lines.Add($"- {line}");
        lines.Add("");

        lines.Add($"## {text["home.page.since"]}");
        lines.Add("");
        var fresh = recent.Take(12).ToList();
        if (fresh.Count == 0)
            lines.Add(text["home.page.quiet"]);
        foreach (var (plugin, subject) in fresh)
            lines.Add($"- **{plugin}**: {subject}");
        lines.Add("");

        return string.Join(Environment.NewLine, lines);
    }
}
