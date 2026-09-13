using System.Globalization;
using System.Text;

namespace Meows.Plugins.Scruff.Services;

/// <summary>
/// The three levels every place recognises in some form. Each destination maps them onto its
/// own words: a self label on Bluesky, a sensitive flag on Mastodon, the rating dropdown on
/// FurAffinity.
/// </summary>
public enum Rating
{
    General,
    Mature,
    Adult,
}

/// <summary>
/// What is being said, once, before each place gets its own version of it.
///
/// One title, one text, one list of tags and one rating. The places differ in how they show
/// each of these, and some have no room for one of them at all, but the person writing the post
/// should only have to write it once.
/// </summary>
public sealed class Draft
{
    public string Title { get; set; } = "";

    public string Text { get; set; } = "";

    public IReadOnlyList<string> Tags { get; set; } = [];

    public Rating Rating { get; set; }

    public bool IsEmpty => Title.Trim().Length == 0 && Text.Trim().Length == 0;
}

/// <summary>
/// Something a place cannot do with the draft as it is. Carried as a key and its values rather
/// than as text so it can be worked out in a test and translated in the tab.
/// </summary>
public sealed record Problem(string Key, params object[] Values);

/// <summary>Tags, written once and spelled the way each place wants them.</summary>
public static class Tags
{
    /// <summary>
    /// Reads what somebody typed into the tags box.
    ///
    /// Commas and new lines separate when there are any, since that is how a tag with a space
    /// in it gets written down. With neither, spaces do, because "cat vore art" was three tags
    /// and nobody wants to be told to add commas. A leading hash is dropped so a pasted hashtag
    /// line reads the same as a typed list.
    /// </summary>
    public static IReadOnlyList<string> Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        var separators = raw.IndexOfAny([',', '\n', ';']) >= 0
            ? new[] { ',', '\n', '\r', ';' }
            : [' ', '\t'];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tags = new List<string>();

        foreach (var piece in raw.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tag = piece.TrimStart('#').Trim();
            if (tag.Length > 0 && seen.Add(tag))
                tags.Add(tag);
        }

        return tags;
    }

    /// <summary>
    /// A tag as a hashtag: "big cat" becomes #BigCat, because a space ends a hashtag and a
    /// capital at each word is what screen readers and most people can still read. Anything
    /// that cannot be in a hashtag is dropped rather than replaced.
    /// </summary>
    public static string Hashtag(string tag)
    {
        var words = tag.Split([' ', '-', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var text = new StringBuilder("#");

        foreach (var word in words)
        {
            var clean = new string(word.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
            if (clean.Length == 0)
                continue;

            text.Append(words.Length > 1 ? char.ToUpperInvariant(clean[0]) + clean[1..] : clean);
        }

        return text.Length > 1 ? text.ToString() : "";
    }

    /// <summary>
    /// A tag as a FurAffinity keyword: spaces become underscores, since the keywords box splits
    /// on spaces, and that is the spelling the site's own search understands.
    /// </summary>
    public static string Keyword(string tag) =>
        string.Join("_", tag.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));

    public static string HashtagLine(IEnumerable<string> tags) =>
        string.Join(" ", tags.Select(Hashtag).Where(h => h.Length > 0));

    public static string KeywordLine(IEnumerable<string> tags) =>
        string.Join(" ", tags.Select(Keyword).Where(k => k.Length > 0));

    /// <summary>
    /// Title, text and a hashtag line, whichever of them there are, with a blank line between.
    /// This is the body most places get, since most places have one box.
    /// </summary>
    public static string Body(Draft draft, bool withHashtags = true)
    {
        var parts = new List<string>();
        if (draft.Title.Trim().Length > 0)
            parts.Add(draft.Title.Trim());
        if (draft.Text.Trim().Length > 0)
            parts.Add(draft.Text.Trim());
        if (withHashtags && draft.Tags.Count > 0)
        {
            var line = HashtagLine(draft.Tags);
            if (line.Length > 0)
                parts.Add(line);
        }

        return string.Join("\n\n", parts);
    }
}

public static class TextLength
{
    /// <summary>
    /// What a person would call the length. Bluesky counts this way, and it is the honest
    /// answer for an emoji that is one picture and six code points.
    /// </summary>
    public static int Graphemes(string text) =>
        text.Length == 0 ? 0 : new StringInfo(text).LengthInTextElements;
}
