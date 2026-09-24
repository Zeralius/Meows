using Meows.Media;
using Meows.Plugins.Scruff.Services;
using Meows.Plugins.Scruff.ViewModels;

namespace Meows.Tests;

/// <summary>
/// Scruff's look before posting: alt text only where the place carries it, a tag only where the
/// place's spelling would lose it, and one strip above Post that says each thing once with the
/// places it applies to, or a tick when there is nothing to say.
/// </summary>
public sealed class LickTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "meows-lick-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    private static Outgoing Picture(string name, string alt = "") =>
        new(name, Preparer.Clean(TestPictures.Jpeg()), alt);

    private static IPostTarget Target(string id) => new ScruffViewModel(new FakeHost(Path.Combine(Path.GetTempPath(), "meows-lick-host-" + Guid.NewGuid().ToString("N")[..8])), new HttpClient())
        .Targets.Single(t => t.Id == id).Target;

    private static string[] Keys(IEnumerable<Problem> notes) => notes.Select(n => n.Key).ToArray();

    [Fact]
    public void Missing_alt_text_is_said_only_where_the_place_carries_it()
    {
        var draft = new Draft { Text = "hello" };
        Outgoing[] none = [Picture("a.jpg"), Picture("b.jpg")];
        Outgoing[] half = [Picture("a.jpg", "A cat, asleep"), Picture("b.jpg")];

        Assert.Equal(["scruff.lick.noalt.all"], Keys(Lick.Notes(Target("bluesky"), draft, none)));
        Assert.Equal(["scruff.lick.noalt.some"], Keys(Lick.Notes(Target("mastodon"), draft, half)));
        Assert.Equal(["scruff.lick.noalt.one"], Keys(Lick.Notes(Target("tumblr"), draft, [Picture("a.jpg")])));
        Assert.Empty(Lick.Notes(Target("bluesky"), draft, [Picture("a.jpg", "A cat")]));
        Assert.Empty(Lick.Notes(Target("furaffinity"), draft, none));
        Assert.Empty(Lick.Notes(Target("x"), draft, none));
    }

    [Fact]
    public void A_tag_is_mentioned_only_where_the_place_would_lose_or_change_it()
    {
        var draft = new Draft { Text = "hello", Tags = ["🐱", "café", "big cat"] };

        var bluesky = Lick.Notes(Target("bluesky"), draft, []);
        Assert.Equal([("scruff.lick.tag.nohashtag", "🐱")], bluesky.Select(n => (n.Key, (string)n.Values[0])));

        var deviant = Lick.Notes(Target("deviantart"), draft, []);
        Assert.Contains(deviant, n => n.Key == "scruff.lick.tag.dropped" && (string)n.Values[0] == "🐱");
        Assert.Contains(deviant, n => n.Key == "scruff.lick.tag.changed" && (string)n.Values[0] == "café" && (string)n.Values[1] == "caf");
        Assert.DoesNotContain(deviant, n => (string)n.Values[0] == "big cat");

        // Tumblr sends tags as they were written and Discord sends none, so neither has anything to lose.
        Assert.Empty(Lick.Notes(Target("tumblr"), draft, []));
        Assert.Empty(Lick.Notes(Target("discord"), draft, []));
    }

    [Fact]
    public void The_strip_says_each_thing_once_with_its_places_and_a_tick_when_there_is_nothing()
    {
        var host = new FakeHost(_root);
        using var model = new ScruffViewModel(host, new HttpClient());
        model.Targets.Single(t => t.Id == "mastodon").IsEnabled = true;
        model.Targets.Single(t => t.Id == "x").IsEnabled = true;

        model.Text = "hello";
        model.TagsText = "cats, 🐱";

        var hashtag = Assert.Single(model.BeforePosting, l => l.Text.Contains("🐱"));
        Assert.Equal("Bluesky, Mastodon, X", hashtag.Places);
        Assert.False(hashtag.IsProblem);
        Assert.False(model.IsAllClear);

        model.TagsText = "cats";
        Assert.Empty(model.BeforePosting);
        Assert.True(model.IsAllClear);

        // A real refusal comes first and is marked as one.
        model.Text = new string('a', 300);
        Assert.True(model.BeforePosting.First().IsProblem);
        Assert.Contains(model.BeforePosting, l => l.IsProblem && l.Places == "X" && l.Text.Contains("280"));

        foreach (var target in model.Targets)
            target.IsEnabled = false;
        Assert.Empty(model.BeforePosting);
        Assert.False(model.IsAllClear);
    }
}
