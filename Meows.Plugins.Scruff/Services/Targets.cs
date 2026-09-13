using Meows.Media;
namespace Meows.Plugins.Scruff.Services;

/// <summary>One picture ready to go, with the words that go with it.</summary>
public sealed record Outgoing(string FileName, Prepared File, string Alt);

/// <summary>What one place would be sent, and whether it can be.</summary>
public sealed record Composed
{
    /// <summary>The body as that place will show it. Empty for a place that takes fields instead.</summary>
    public string Text { get; init; } = "";

    public int Length { get; init; }

    public int Limit { get; init; }

    /// <summary>What stands in the way. Empty means it can go.</summary>
    public IReadOnlyList<Problem> Problems { get; init; } = [];

    /// <summary>Worth knowing, not worth stopping for: a GIF that will go as a still, say.</summary>
    public IReadOnlyList<Problem> Notes { get; init; } = [];

    public bool CanGo => Problems.Count == 0;
}

/// <summary>How a post went.</summary>
public sealed record PostResult(bool Ok, string? Url, string? Error)
{
    public static PostResult Posted(string? url) => new(true, url, null);

    public static PostResult Failed(string error) => new(false, null, error);
}

/// <summary>
/// One thing to paste into a form. The label is a string key, since the sheet is worked out
/// away from the tab and translated when it is shown.
/// </summary>
public sealed record HandoffField(string LabelKey, string Value);

/// <summary>
/// What is on the sheet for a place that has to be posted to by hand: where to go, what to
/// paste, and what to remember to click, such as the rating.
/// </summary>
public sealed record HandoffSheet(string Url, IReadOnlyList<HandoffField> Fields, IReadOnlyList<Problem> Reminders);

/// <summary>
/// One place a post can go.
///
/// Two kinds share this shape. A place with an API that can be spoken to from here posts by
/// itself. A place without one gets a hand-off instead: the files land in a folder, the browser
/// opens on the upload page, and every field is beside a copy button. The tab treats both the
/// same way up to the moment of posting, which is the point of having one shape.
/// </summary>
public interface IPostTarget
{
    /// <summary>Stable. It is the settings key and the name of the stored credential.</summary>
    string Id { get; }

    string Name { get; }

    MediaLimits Limits { get; }

    /// <summary>Whether this place can be posted to from here, or only opened.</summary>
    bool PostsItself { get; }

    /// <summary>Whether a post here can be words alone.</summary>
    bool TakesTextOnly { get; }

    /// <summary>
    /// What this place would be sent, worked out from the draft and the pictures. Pure, so the
    /// tab can show it as the draft is typed and a test can check it without a network.
    /// </summary>
    Composed Compose(Draft draft, IReadOnlyList<Outgoing> images);
}

/// <summary>A place that can be posted to directly.</summary>
public interface IApiTarget : IPostTarget
{
    /// <summary>Whether the credential it needs has been given.</summary>
    bool IsSignedIn { get; }

    /// <summary>Who it is signed in as, for the card. Null when it is not.</summary>
    string? Account { get; }

    Task<PostResult> PostAsync(Draft draft, IReadOnlyList<Outgoing> images, CancellationToken token);
}

/// <summary>A place that has to be opened in a browser and filled in by hand.</summary>
public interface IManualTarget : IPostTarget
{
    HandoffSheet Sheet(Draft draft, IReadOnlyList<Outgoing> images);
}

/// <summary>The checks every place makes before its own.</summary>
public static class Checks
{
    /// <summary>
    /// What stops the draft going to a place, and what is merely worth saying.
    ///
    /// Called twice in a post's life: while the draft is being written, on the cleaned files, and
    /// again at posting, on the fitted ones. A file over the byte limit is only a problem when
    /// fitting cannot fix it, which is a GIF, a file that could not be decoded, or one that has
    /// been through fitting already and is still too big.
    /// </summary>
    public static (List<Problem> Problems, List<Problem> Notes) Common(IPostTarget target, Draft draft, IReadOnlyList<Outgoing> images)
    {
        var problems = new List<Problem>();
        var notes = new List<Problem>();
        var limits = target.Limits;

        if (images.Count == 0 && (!target.TakesTextOnly || draft.IsEmpty))
            problems.Add(new Problem("scruff.problem.nothing"));

        if (limits.MaxImages > 0 && images.Count > limits.MaxImages)
            problems.Add(new Problem("scruff.problem.toomany", limits.MaxImages, images.Count));

        foreach (var image in images)
        {
            var file = image.File;
            var stuck = file.Unreadable || file.Format == ImageFormat.Unknown;

            if (!limits.Takes(file.Format))
            {
                if (stuck)
                    problems.Add(new Problem("scruff.problem.format", image.FileName, file.Extension.TrimStart('.')));
                else if (file.Format == ImageFormat.Gif)
                    notes.Add(new Problem("scruff.note.gifstill", image.FileName));
            }
            else if (limits.MaxBytes > 0 && file.Bytes.LongLength > limits.MaxBytes)
            {
                if (stuck || file.Format == ImageFormat.Gif || file.Fitted)
                    problems.Add(new Problem("scruff.problem.toobig", image.FileName, Humanise(limits.MaxBytes)));
                else
                    notes.Add(new Problem("scruff.note.shrink", image.FileName, Humanise(limits.MaxBytes)));
            }
        }

        return (problems, notes);
    }

    public static string Humanise(long bytes) => bytes switch
    {
        >= 1_000_000 => $"{bytes / 1_000_000.0:0.#} MB",
        >= 1_000 => $"{bytes / 1_000.0:0} kB",
        _ => $"{bytes} B",
    };
}
