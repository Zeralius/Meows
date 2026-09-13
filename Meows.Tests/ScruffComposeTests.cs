using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meows.Plugins.Scruff.Services;

namespace Meows.Tests;

/// <summary>Tags written once, spelled for each place.</summary>
public class ScruffTagTests
{
    [Fact]
    public void Commas_separate_when_there_are_any_and_spaces_when_there_are_not()
    {
        Assert.Equal(["big cat", "vore", "art"], Tags.Parse("big cat, vore, art"));
        Assert.Equal(["cat", "vore", "art"], Tags.Parse("cat vore art"));
        Assert.Equal(["one", "two"], Tags.Parse("one\ntwo"));
    }

    [Fact]
    public void A_pasted_hashtag_line_reads_the_same_as_a_typed_list()
    {
        Assert.Equal(["furry", "art"], Tags.Parse("#furry #art"));
        Assert.Equal(["furry", "Art"], Tags.Parse("#furry, #Art, furry"));
    }

    [Fact]
    public void Hashtags_join_words_with_a_capital_and_keywords_with_an_underscore()
    {
        Assert.Equal("#BigCat", Tags.Hashtag("big cat"));
        Assert.Equal("#vore", Tags.Hashtag("vore"));
        Assert.Equal("#snake_tail", Tags.Hashtag("snake_tail"));
        Assert.Equal("", Tags.Hashtag("!!!"));

        Assert.Equal("big_cat", Tags.Keyword("big cat"));
        Assert.Equal("big_cat", Tags.Keyword("big  cat"));
    }

    [Fact]
    public void The_body_is_title_text_and_hashtags_with_a_line_between()
    {
        var draft = new Draft { Title = "Lunch", Text = "Someone got eaten.", Tags = ["vore", "big cat"] };

        Assert.Equal("Lunch\n\nSomeone got eaten.\n\n#vore #BigCat", Tags.Body(draft));
        Assert.Equal("Lunch\n\nSomeone got eaten.", Tags.Body(draft, withHashtags: false));
    }

    [Fact]
    public void An_emoji_is_one_grapheme_however_many_code_points_it_is()
    {
        Assert.Equal(1, TextLength.Graphemes("👨‍👩‍👧"));
        Assert.Equal(5, TextLength.Graphemes("héllo"));
        Assert.Equal(0, TextLength.Graphemes(""));
    }
}

/// <summary>The Bluesky record, checked without a network.</summary>
public class ScruffBlueskyTests
{
    [Fact]
    public void Hashtag_facets_are_byte_ranges_not_character_ranges()
    {
        // The umlaut is two bytes in UTF-8, so every offset after it is one further along than
        // the character index says.
        var text = "Grüße!\n\n#Vore #BigCat";

        var facets = BlueskyTarget.Facets(text);

        Assert.Equal(2, facets.Count);

        var first = facets[0]!["index"]!;
        var start = first["byteStart"]!.GetValue<int>();
        var end = first["byteEnd"]!.GetValue<int>();
        Assert.Equal("#Vore", Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text)[start..end]));
        Assert.Equal("Vore", facets[0]!["features"]![0]!["tag"]!.GetValue<string>());

        var second = facets[1]!["index"]!;
        var s2 = second["byteStart"]!.GetValue<int>();
        var e2 = second["byteEnd"]!.GetValue<int>();
        Assert.Equal("#BigCat", Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text)[s2..e2]));
    }

    [Fact]
    public void A_link_becomes_a_facet_without_its_trailing_full_stop()
    {
        var text = "See https://example.com/page.";

        var facet = Assert.Single(BlueskyTarget.Facets(text));

        Assert.Equal("https://example.com/page", facet!["features"]![0]!["uri"]!.GetValue<string>());
        Assert.Equal(text.Length - 1, facet["index"]!["byteEnd"]!.GetValue<int>());
    }

    [Fact]
    public void The_rating_becomes_a_self_label()
    {
        var adult = BlueskyTarget.BuildRecord("hi", Rating.Adult, [], DateTimeOffset.UnixEpoch);
        var mature = BlueskyTarget.BuildRecord("hi", Rating.Mature, [], DateTimeOffset.UnixEpoch);
        var general = BlueskyTarget.BuildRecord("hi", Rating.General, [], DateTimeOffset.UnixEpoch);

        Assert.Equal("porn", adult["labels"]!["values"]![0]!["val"]!.GetValue<string>());
        Assert.Equal("sexual", mature["labels"]!["values"]![0]!["val"]!.GetValue<string>());
        Assert.Null(general["labels"]);
        Assert.Equal("1970-01-01T00:00:00.000Z", general["createdAt"]!.GetValue<string>());
    }

    [Fact]
    public void Pictures_go_in_an_images_embed()
    {
        var blob = new JsonObject { ["$type"] = "blob", ["ref"] = new JsonObject { ["$link"] = "bafy" } };
        var image = new JsonObject { ["alt"] = "a cat", ["image"] = blob };

        var record = BlueskyTarget.BuildRecord("look", Rating.General, [image], DateTimeOffset.UnixEpoch);

        Assert.Equal("app.bsky.embed.images", record["embed"]!["$type"]!.GetValue<string>());
        Assert.Equal("a cat", record["embed"]!["images"]![0]!["alt"]!.GetValue<string>());
        Assert.Null(record["facets"]);
    }

    [Fact]
    public void The_session_names_the_server_the_account_lives_on()
    {
        const string json = """
            {
              "accessJwt": "jwt", "did": "did:plc:abc", "handle": "me.bsky.social",
              "didDoc": { "service": [
                { "id": "#atproto_pds", "type": "AtprotoPersonalDataServer", "serviceEndpoint": "https://oyster.us-east.host.bsky.network/" }
              ] }
            }
            """;

        var session = BlueskyTarget.ParseSession(json);

        Assert.Equal("https://oyster.us-east.host.bsky.network", session.Pds);
        Assert.Equal("did:plc:abc", session.Did);
        Assert.Equal("me.bsky.social", session.Handle);
    }

    [Fact]
    public void Without_a_did_document_the_entryway_is_used()
    {
        var session = BlueskyTarget.ParseSession("""{"accessJwt":"j","did":"did:plc:x","handle":"h"}""");

        Assert.Equal(BlueskyTarget.Entryway, session.Pds);
    }

    [Fact]
    public void The_web_link_is_built_from_the_record_key()
    {
        Assert.Equal("https://bsky.app/profile/me.bsky.social/post/3kabc",
            BlueskyTarget.WebLink("me.bsky.social", "at://did:plc:abc/app.bsky.feed.post/3kabc"));
    }
}

/// <summary>What each place says about a draft before anything is sent.</summary>
public class ScruffComposeTests
{
    private static Outgoing Picture(string name = "a.jpg", ImageFormat format = ImageFormat.Jpeg, int bytes = 1000, bool fitted = false) =>
        new(name, new Prepared
        {
            Bytes = new byte[bytes],
            Format = format,
            Width = 100,
            Height = 100,
            Original = new MetadataReport { Format = format },
            Fitted = fitted,
        }, "");

    private static Draft Draft(string title = "T", string text = "hello", params string[] tags) =>
        new() { Title = title, Text = text, Tags = tags };

    [Fact]
    public void Bluesky_counts_graphemes_against_three_hundred()
    {
        var target = new BlueskyTarget(new HttpClient());

        var fine = target.Compose(Draft(text: new string('a', 290)), [Picture()]);
        var over = target.Compose(Draft(text: new string('a', 300)), [Picture()]);

        Assert.True(fine.CanGo);
        Assert.Equal(300, fine.Limit);
        Assert.False(over.CanGo);
        Assert.Contains(over.Problems, p => p.Key == "scruff.problem.toolong");
    }

    [Fact]
    public void Too_many_pictures_is_a_problem_not_a_silent_cut()
    {
        var target = new BlueskyTarget(new HttpClient());
        var five = Enumerable.Range(0, 5).Select(i => Picture($"{i}.jpg")).ToList();

        var composed = target.Compose(Draft(), five);

        var problem = Assert.Single(composed.Problems);
        Assert.Equal("scruff.problem.toomany", problem.Key);
        Assert.Equal([4, 5], problem.Values);
    }

    [Fact]
    public void A_big_file_is_a_note_before_fitting_and_a_problem_after()
    {
        var target = new BlueskyTarget(new HttpClient());

        var before = target.Compose(Draft(), [Picture(bytes: 2_000_000)]);
        var after = target.Compose(Draft(), [Picture(bytes: 2_000_000, fitted: true)]);

        Assert.True(before.CanGo);
        Assert.Contains(before.Notes, n => n.Key == "scruff.note.shrink");
        Assert.False(after.CanGo);
        Assert.Contains(after.Problems, p => p.Key == "scruff.problem.toobig");
    }

    [Fact]
    public void A_gif_over_the_limit_is_a_problem_straight_away()
    {
        var target = new BlueskyTarget(new HttpClient());

        var composed = target.Compose(Draft(), [Picture("anim.gif", ImageFormat.Gif, 2_000_000)]);

        Assert.False(composed.CanGo);
    }

    [Fact]
    public void Words_alone_are_fine_where_words_alone_are_allowed()
    {
        Assert.True(new BlueskyTarget(new HttpClient()).Compose(Draft(), []).CanGo);
        Assert.True(new MastodonTarget(new HttpClient()).Compose(Draft(), []).CanGo);
        Assert.False(new FurAffinityTarget().Compose(Draft(), []).CanGo);
        Assert.False(new BlueskyTarget(new HttpClient()).Compose(Draft("", ""), []).CanGo);
    }

    [Fact]
    public void Mastodon_reads_the_limit_the_server_states()
    {
        Assert.Equal(5000, MastodonTarget.ReadMaxCharacters("""{"configuration":{"statuses":{"max_characters":5000}}}"""));
        Assert.Equal(500, MastodonTarget.ReadMaxCharacters("{}"));
        Assert.Equal(500, MastodonTarget.ReadMaxCharacters("not json"));
    }

    [Fact]
    public void A_server_typed_any_old_way_becomes_a_base_url()
    {
        Assert.Equal("https://mastodon.social", new MastodonLogin("mastodon.social", "t").BaseUrl);
        Assert.Equal("https://mastodon.social", new MastodonLogin("https://mastodon.social/", "t").BaseUrl);
        Assert.Equal("https://meow.social", new MastodonLogin("@meow.social", "t").BaseUrl);
        Assert.Equal("meow.social", new MastodonLogin("meow.social", "t").Host);
    }

    [Fact]
    public void FurAffinity_wants_a_title_and_spells_keywords_with_underscores()
    {
        var target = new FurAffinityTarget();
        var draft = Draft("Lunch", "Someone got eaten.", "big cat", "vore");

        var sheet = target.Sheet(draft, [Picture(), Picture("b.jpg")]);

        Assert.Equal("https://www.furaffinity.net/submit/", sheet.Url);
        Assert.Equal("big_cat vore", sheet.Fields.Single(f => f.LabelKey == "scruff.field.keywords").Value);
        Assert.Contains(sheet.Reminders, r => r.Key == "scruff.remind.onebyone" && (int)r.Values[0] == 2);

        Assert.False(target.Compose(Draft(title: ""), [Picture()]).CanGo);
    }

    [Fact]
    public void FurAffinity_does_not_take_webp_but_that_is_fixed_by_fitting()
    {
        var composed = new FurAffinityTarget().Compose(Draft(), [Picture("a.webp", ImageFormat.WebP)]);

        Assert.True(composed.CanGo);
    }

    [Fact]
    public void X_carries_the_text_in_the_link()
    {
        var sheet = new XTarget().Sheet(Draft("Hi", "there", "cat"), [Picture()]);

        Assert.StartsWith("https://x.com/intent/post?text=", sheet.Url);
        Assert.Equal("Hi\n\nthere\n\n#cat", Uri.UnescapeDataString(sheet.Url["https://x.com/intent/post?text=".Length..]));
    }

    [Fact]
    public void Instagram_refuses_adult_work_and_warns_about_odd_shapes()
    {
        var target = new InstagramTarget();
        var adult = Draft();
        adult.Rating = Rating.Adult;

        Assert.Contains(target.Compose(adult, [Picture()]).Problems, p => p.Key == "scruff.problem.noadult");

        var tall = Picture() with { File = Picture().File with { Width = 100, Height = 300 } };
        Assert.Contains(target.Sheet(Draft(), [tall]).Reminders, r => r.Key == "scruff.remind.crop");
        Assert.Empty(target.Sheet(Draft(), [Picture()]).Reminders);
    }

    [Fact]
    public void Reddit_opens_the_subreddit_and_takes_the_first_line_as_a_title()
    {
        var target = new RedditTarget { Subreddit = "r/furry" };

        var sheet = target.Sheet(Draft("", "First line\nSecond line"), [Picture()]);

        Assert.Equal("https://www.reddit.com/r/furry/submit?type=IMAGE", sheet.Url);
        Assert.Equal("First line", sheet.Fields[0].Value);

        target.Subreddit = "";
        Assert.Equal("https://www.reddit.com/submit?type=IMAGE", target.Sheet(Draft(), [Picture()]).Url);
    }

    [Fact]
    public void Ratings_become_reminders_where_they_cannot_be_set_from_here()
    {
        var draft = Draft();
        draft.Rating = Rating.Adult;

        Assert.Contains(new FurAffinityTarget().Sheet(draft, [Picture()]).Reminders, r => r.Key == "scruff.remind.rating.adult");
        Assert.Contains(new RedditTarget().Sheet(draft, [Picture()]).Reminders, r => r.Key == "scruff.remind.nsfw");
    }
}

/// <summary>The one credential store. Windows only, like the app.</summary>
public class ScruffSecretsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "meows-secrets-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void What_is_saved_comes_back_and_is_not_on_disk_in_clear()
    {
        var secrets = new Secrets(_root);

        secrets.Save("bluesky", "{\"password\":\"xxxx-yyyy-zzzz\"}");

        Assert.True(secrets.Has("bluesky"));
        Assert.Equal("{\"password\":\"xxxx-yyyy-zzzz\"}", secrets.Load("bluesky"));

        var raw = File.ReadAllBytes(Path.Combine(_root, "bluesky.secret"));
        Assert.DoesNotContain("xxxx-yyyy-zzzz", Encoding.UTF8.GetString(raw));
        Assert.DoesNotContain("xxxx-yyyy-zzzz", Encoding.Unicode.GetString(raw));

        secrets.Forget("bluesky");
        Assert.False(secrets.Has("bluesky"));
        Assert.Null(secrets.Load("bluesky"));
    }

    [Fact]
    public void A_file_that_cannot_be_opened_reads_as_nothing_rather_than_throwing()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "mastodon.secret"), [1, 2, 3, 4]);

        Assert.Null(new Secrets(_root).Load("mastodon"));
    }
}
