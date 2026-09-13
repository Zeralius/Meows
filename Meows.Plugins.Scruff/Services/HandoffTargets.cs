namespace Meows.Plugins.Scruff.Services;

/// <summary>
/// The places without a door.
///
/// FurAffinity has no API at all. Instagram's needs a business account tied to a Facebook page
/// and a picture already on a public web server, which is nothing a desktop app has. X will
/// take a post through its API but only from a registered developer app with signed requests,
/// which is a project of its own and one that has changed its terms three times in as many
/// years. Reddit's works for personal use but wants an app registration and a two step upload,
/// and is on the list to do properly.
///
/// So these are handed off rather than posted: the cleaned files land in a folder that is opened
/// beside the browser, the browser opens on the upload page, and every field the site has is on
/// a sheet with a copy button beside it, spelled the way that site spells it. The typing is
/// gone, the metadata is gone, and what is left is clicking the file and pasting.
/// </summary>
public abstract class HandoffTarget : IHandoffTarget
{
    public abstract string Id { get; }

    public abstract string Name { get; }

    public abstract MediaLimits Limits { get; }

    public bool PostsItself => false;

    public virtual bool TakesTextOnly => true;

    public abstract Composed Compose(Draft draft, IReadOnlyList<Outgoing> images);

    public abstract HandoffSheet Sheet(Draft draft, IReadOnlyList<Outgoing> images);

    protected static Problem? RatingReminder(Rating rating) => rating switch
    {
        Rating.Adult => new Problem("scruff.remind.rating.adult"),
        Rating.Mature => new Problem("scruff.remind.rating.mature"),
        _ => null,
    };

    protected static List<Problem> Reminders(Draft draft, params Problem?[] extra)
    {
        var list = new List<Problem>();
        if (RatingReminder(draft.Rating) is { } rating)
            list.Add(rating);
        list.AddRange(extra.Where(p => p is not null)!);
        return list;
    }
}

/// <summary>
/// FurAffinity. Title, description and keywords are three boxes, keywords are separated by
/// spaces with underscores inside a tag, and each picture is its own submission, so the sheet
/// says so when there is more than one.
/// </summary>
public sealed class FurAffinityTarget : HandoffTarget
{
    public override string Id => "furaffinity";

    public override string Name => "FurAffinity";

    public override MediaLimits Limits { get; } = new(0, 10_000_000, 0, [ImageFormat.Jpeg, ImageFormat.Png, ImageFormat.Gif]);

    public override bool TakesTextOnly => false;

    public override Composed Compose(Draft draft, IReadOnlyList<Outgoing> images)
    {
        var (problems, notes) = Checks.Common(this, draft, images);
        if (draft.Title.Trim().Length == 0 && images.Count > 0)
            problems.Add(new Problem("scruff.problem.notitle"));

        return new Composed { Text = Tags.KeywordLine(draft.Tags), Problems = problems, Notes = notes };
    }

    public override HandoffSheet Sheet(Draft draft, IReadOnlyList<Outgoing> images)
    {
        var fields = new List<HandoffField>
        {
            new("scruff.field.title", draft.Title.Trim()),
            new("scruff.field.description", draft.Text.Trim()),
            new("scruff.field.keywords", Tags.KeywordLine(draft.Tags)),
        };

        return new HandoffSheet(
            "https://www.furaffinity.net/submit/",
            fields,
            Reminders(draft, images.Count > 1 ? new Problem("scruff.remind.onebyone", images.Count) : null));
    }
}

/// <summary>
/// X. The intent link carries the text into the compose box, so all that is left by hand is
/// attaching the pictures. The 280 is characters as X counts them, roughly: a link is always
/// 23 and some scripts count double, and neither refinement is worth carrying here.
/// </summary>
public sealed class XTarget : HandoffTarget
{
    public const int MaxCharacters = 280;

    public override string Id => "x";

    public override string Name => "X";

    public override MediaLimits Limits { get; } = new(4, 5_000_000, 4096, [ImageFormat.Jpeg, ImageFormat.Png, ImageFormat.WebP, ImageFormat.Gif]);

    public override Composed Compose(Draft draft, IReadOnlyList<Outgoing> images)
    {
        var text = Tags.Body(draft);
        var length = TextLength.Graphemes(text);
        var (problems, notes) = Checks.Common(this, draft, images);
        if (length > MaxCharacters)
            problems.Add(new Problem("scruff.problem.toolong", MaxCharacters, length));

        return new Composed { Text = text, Length = length, Limit = MaxCharacters, Problems = problems, Notes = notes };
    }

    public override HandoffSheet Sheet(Draft draft, IReadOnlyList<Outgoing> images)
    {
        var text = Tags.Body(draft);
        return new HandoffSheet(
            "https://x.com/intent/post?text=" + Uri.EscapeDataString(text),
            [new HandoffField("scruff.field.text", text)],
            Reminders(draft));
    }
}

/// <summary>
/// Instagram, through its website, which does take uploads from a desktop browser these days.
/// Thirty hashtags is the ceiling, and the site crops anything taller than 4:5 or wider than
/// 1.91:1, which is worth knowing before rather than after.
/// </summary>
public sealed class InstagramTarget : HandoffTarget
{
    public const int MaxCharacters = 2200;

    public const int MaxHashtags = 30;

    public override string Id => "instagram";

    public override string Name => "Instagram";

    public override MediaLimits Limits { get; } = new(10, 8_000_000, 1440, [ImageFormat.Jpeg, ImageFormat.Png]);

    public override bool TakesTextOnly => false;

    public override Composed Compose(Draft draft, IReadOnlyList<Outgoing> images)
    {
        var text = Tags.Body(draft);
        var length = TextLength.Graphemes(text);
        var (problems, notes) = Checks.Common(this, draft, images);
        if (length > MaxCharacters)
            problems.Add(new Problem("scruff.problem.toolong", MaxCharacters, length));
        if (draft.Tags.Count > MaxHashtags)
            problems.Add(new Problem("scruff.problem.toomanytags", MaxHashtags, draft.Tags.Count));

        // There is no rating to set, because there is no rating under which it is allowed. Saying
        // so here is kinder than the account being closed after the third one.
        if (draft.Rating == Rating.Adult)
            problems.Add(new Problem("scruff.problem.noadult"));

        return new Composed { Text = text, Length = length, Limit = MaxCharacters, Problems = problems, Notes = notes };
    }

    public override HandoffSheet Sheet(Draft draft, IReadOnlyList<Outgoing> images)
    {
        var odd = images.Any(i => i.File.Width > 0 && i.File.Height > 0 && !FitsInstagram(i.File.Width, i.File.Height));
        return new HandoffSheet(
            "https://www.instagram.com/",
            [new HandoffField("scruff.field.caption", Tags.Body(draft))],
            odd ? [new Problem("scruff.remind.crop")] : []);
    }

    public static bool FitsInstagram(int width, int height)
    {
        var ratio = width / (double)height;
        return ratio >= 0.8 - 0.001 && ratio <= 1.91 + 0.001;
    }
}

/// <summary>
/// Reddit. The title is the post on an image post, and the subreddit is the one setting that
/// is remembered, because it decides which page opens. Doing this through the API is on the
/// list; until then the page opens and the title is a paste away.
/// </summary>
public sealed class RedditTarget : HandoffTarget
{
    public const int MaxTitle = 300;

    public override string Id => "reddit";

    public override string Name => "Reddit";

    public override MediaLimits Limits { get; } = new(20, 20_000_000, 0, [ImageFormat.Jpeg, ImageFormat.Png, ImageFormat.Gif]);

    /// <summary>Without the r/. Empty opens the general submit page and lets the site ask.</summary>
    public string Subreddit { get; set; } = "";

    public override Composed Compose(Draft draft, IReadOnlyList<Outgoing> images)
    {
        var title = TitleFor(draft);
        var (problems, notes) = Checks.Common(this, draft, images);
        if (title.Length == 0)
            problems.Add(new Problem("scruff.problem.notitle"));
        else if (title.Length > MaxTitle)
            problems.Add(new Problem("scruff.problem.toolong", MaxTitle, title.Length));

        return new Composed { Text = title, Length = title.Length, Limit = MaxTitle, Problems = problems, Notes = notes };
    }

    /// <summary>The title, or the first line of the text when there is no title.</summary>
    public static string TitleFor(Draft draft)
    {
        if (draft.Title.Trim().Length > 0)
            return draft.Title.Trim();

        var first = draft.Text.Split('\n', 2, StringSplitOptions.TrimEntries)[0];
        return first;
    }

    public override HandoffSheet Sheet(Draft draft, IReadOnlyList<Outgoing> images)
    {
        var sub = Subreddit.Trim().TrimStart('/').Replace("r/", "", StringComparison.OrdinalIgnoreCase).Trim('/');
        var url = sub.Length > 0
            ? $"https://www.reddit.com/r/{Uri.EscapeDataString(sub)}/submit?type=IMAGE"
            : "https://www.reddit.com/submit?type=IMAGE";

        var fields = new List<HandoffField> { new("scruff.field.title", TitleFor(draft)) };
        if (draft.Text.Trim().Length > 0)
            fields.Add(new HandoffField("scruff.field.text", draft.Text.Trim()));

        // Reddit has one switch rather than three ratings, so the reminder is about the switch.
        return new HandoffSheet(url, fields,
            draft.Rating != Rating.General ? [new Problem("scruff.remind.nsfw")] : []);
    }
}
