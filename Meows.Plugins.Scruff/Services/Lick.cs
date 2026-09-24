namespace Meows.Plugins.Scruff.Services;

/// <summary>How a place writes the draft's tags, which decides whether a tag can survive the trip.</summary>
public enum TagSpelling
{
    /// <summary>Tags do not go anywhere of their own, or go exactly as written.</summary>
    None,

    /// <summary>As hashtags in the text: a space ends one, and anything but letters, digits and _ is dropped.</summary>
    Hashtags,

    /// <summary>As keywords split on spaces, the space becoming an underscore.</summary>
    Keywords,

    /// <summary>Keywords with everything outside plain ASCII letters and digits dropped, which is DeviantArt.</summary>
    AsciiKeywords,
}

/// <summary>
/// The look before posting: what will go wrong on a place, or go but not as meant, worked out
/// from the draft and the pictures in memory and never from the network. These are notes, never
/// blocks; what would actually be refused is each place's own <see cref="Composed.Problems"/>.
///
/// Only what is true for that place: alt text only where the place carries it, a tag only where
/// the way the place spells tags would lose it. A wall of warnings is not read, so nothing is
/// said twice and nothing is said that the place would not act on.
/// </summary>
public static class Lick
{
    public static List<Problem> Notes(IPostTarget target, Draft draft, IReadOnlyList<Outgoing> images)
    {
        var notes = new List<Problem>();

        if (target.CarriesAltText && images.Count > 0)
        {
            var missing = images.Count(i => string.IsNullOrWhiteSpace(i.Alt));
            if (missing == images.Count)
                notes.Add(new Problem(images.Count == 1 ? "scruff.lick.noalt.one" : "scruff.lick.noalt.all", images.Count));
            else if (missing > 0)
                notes.Add(new Problem("scruff.lick.noalt.some", missing, images.Count));
        }

        foreach (var tag in draft.Tags)
        {
            switch (target.Spelling)
            {
                case TagSpelling.Hashtags when Tags.Hashtag(tag).Length == 0:
                    notes.Add(new Problem("scruff.lick.tag.nohashtag", tag));
                    break;
                case TagSpelling.Keywords when Tags.Keyword(tag).Length == 0:
                    notes.Add(new Problem("scruff.lick.tag.dropped", tag));
                    break;
                case TagSpelling.AsciiKeywords:
                {
                    var kept = DeviantArtTarget.CleanTags([tag]);
                    if (kept.Count == 0)
                        notes.Add(new Problem("scruff.lick.tag.dropped", tag));
                    else if (!string.Equals(kept[0], Tags.Keyword(tag), StringComparison.OrdinalIgnoreCase))
                        notes.Add(new Problem("scruff.lick.tag.changed", tag, kept[0]));
                    break;
                }
            }
        }

        return notes;
    }
}
