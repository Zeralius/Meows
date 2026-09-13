using System.Net;
using System.Text.Json;
using Meows.Media;
using Meows.Plugins.Scruff.Services;

namespace Meows.Tests;

/// <summary>The OAuth dance, minus the browser and the service.</summary>
public class ScruffOAuthFlowTests
{
    private static readonly OAuthEndpoints Endpoints = new("https://example.com/authorize", "https://example.com/token", "basic write");

    [Fact]
    public void The_authorize_link_carries_the_app_the_redirect_the_scope_and_the_state()
    {
        var url = OAuthFlow.AuthorizeUrl(Endpoints, new OAuthApp(" id ", "secret"), "abc");

        Assert.StartsWith("https://example.com/authorize?response_type=code&client_id=id", url);
        Assert.Contains("&redirect_uri=http%3A%2F%2Flocalhost%3A41597%2Fcallback", url);
        Assert.Contains("&scope=basic%20write", url);
        Assert.EndsWith("&state=abc", url);
        Assert.DoesNotContain("secret", url);
    }

    [Fact]
    public void The_callback_is_read_off_the_request_line()
    {
        Assert.Equal(("c0de", "s", null), OAuthFlow.ParseCallback("GET /callback?code=c0de&state=s HTTP/1.1"));
        Assert.Equal((null, "s", "access_denied: The user said no"),
            OAuthFlow.ParseCallback("GET /callback?error=access_denied&error_description=The+user+said+no&state=s HTTP/1.1"));
        Assert.Equal((null, null, "no query"), OAuthFlow.ParseCallback("GET /favicon.ico HTTP/1.1"));
    }

    [Fact]
    public void Tokens_keep_the_old_refresh_token_when_none_comes_back()
    {
        var now = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

        var fresh = OAuthFlow.ParseTokens("""{"access_token":"a","refresh_token":"r","expires_in":3600}""", now);
        var renewed = OAuthFlow.ParseTokens("""{"access_token":"b","expires_in":60}""", now, previousRefreshToken: "r");

        Assert.Equal("r", fresh.RefreshToken);
        Assert.Equal(now.AddHours(1), fresh.ExpiresAt);
        Assert.True(fresh.IsFresh(now));
        Assert.Equal("r", renewed.RefreshToken);
        // Sixty seconds out is inside the minute of slack, so it already counts as stale.
        Assert.False(renewed.IsFresh(now));
    }

    [Fact]
    public async Task The_listener_takes_the_right_state_and_waves_off_the_wrong_one()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var waiting = OAuthFlow.WaitForCodeAsync("good", TimeSpan.FromSeconds(15), cancel.Token);

        using var http = new HttpClient();
        var wrong = await http.GetAsync($"{OAuthFlow.RedirectUri}?code=nope&state=stale", cancel.Token);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.False(waiting.IsCompleted);

        var right = await http.GetAsync($"{OAuthFlow.RedirectUri}?code=yes&state=good", cancel.Token);
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
        Assert.Contains("close this tab", await right.Content.ReadAsStringAsync(cancel.Token));

        Assert.Equal("yes", await waiting);
    }

    [Fact]
    public async Task A_refusal_in_the_browser_is_the_error_on_this_side()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var waiting = OAuthFlow.WaitForCodeAsync("s", TimeSpan.FromSeconds(15), cancel.Token);

        using var http = new HttpClient();
        await http.GetAsync($"{OAuthFlow.RedirectUri}?error=access_denied&state=s", cancel.Token);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => waiting);
        Assert.Equal("access_denied", ex.Message);
    }

    [Fact]
    public void Signed_in_means_a_refresh_token_or_a_token_that_still_works()
    {
        var target = new DeviantArtTarget(new HttpClient());
        Assert.False(target.IsSignedIn);

        target.App = new OAuthApp("i", "s");
        target.Tokens = new OAuthTokens("a", null, DateTimeOffset.UtcNow.AddHours(-1));
        Assert.False(target.IsSignedIn);

        target.Tokens = new OAuthTokens("a", "r", DateTimeOffset.UtcNow.AddHours(-1));
        Assert.True(target.IsSignedIn);

        target.SignOut();
        Assert.False(target.IsSignedIn);
        Assert.Null(target.Account);
    }
}

/// <summary>The three places added in 2.1, checked without a network.</summary>
public class ScruffNewPlacesTests
{
    private static Outgoing Picture(string name = "a.jpg", string alt = "") =>
        new(name, new Prepared
        {
            Bytes = new byte[10],
            Format = ImageFormat.Jpeg,
            Width = 10,
            Height = 10,
            Original = new MetadataReport { Format = ImageFormat.Jpeg },
        }, alt);

    private static Draft Draft(string title = "T", string text = "hello", Rating rating = Rating.General, params string[] tags) =>
        new() { Title = title, Text = text, Tags = tags, Rating = rating };

    // ---- Discord ------------------------------------------------------------------------------

    [Fact]
    public void Discord_takes_a_webhook_url_and_nothing_else()
    {
        Assert.True(DiscordTarget.IsWebhookUrl("https://discord.com/api/webhooks/123/abc-def"));
        Assert.True(DiscordTarget.IsWebhookUrl("https://discordapp.com/api/webhooks/123/abc"));
        Assert.True(DiscordTarget.IsWebhookUrl("https://ptb.discord.com/api/webhooks/123/abc"));
        Assert.False(DiscordTarget.IsWebhookUrl("http://discord.com/api/webhooks/123/abc"));
        Assert.False(DiscordTarget.IsWebhookUrl("https://example.com/api/webhooks/123/abc"));
        Assert.False(DiscordTarget.IsWebhookUrl("https://discord.com/channels/1/2"));
    }

    [Fact]
    public void Discord_names_the_card_after_the_webhook_and_links_the_message()
    {
        var webhook = DiscordTarget.ParseWebhook("https://discord.com/api/webhooks/1/t",
            """{"name":"Gallery","channel_id":"22","guild_id":"33","id":"1"}""");

        Assert.Equal("Gallery", webhook.Name);
        Assert.Equal("https://discord.com/channels/33/22/44", DiscordTarget.MessageLink(webhook, "44"));
        Assert.Null(DiscordTarget.MessageLink(webhook, null));
    }

    [Fact]
    public void Discord_has_no_hashtags_and_rated_pictures_are_spoilers()
    {
        var target = new DiscordTarget(new HttpClient());

        var general = target.Compose(Draft(tags: ["big cat"]), [Picture()]);
        var mature = target.Compose(Draft(rating: Rating.Mature), [Picture()]);

        Assert.Equal("T\n\nhello", general.Text);
        Assert.Equal(2000, general.Limit);
        Assert.Empty(general.Notes);
        Assert.Contains(mature.Notes, n => n.Key == "scruff.note.spoiler");

        Assert.Equal(["SPOILER_a.jpg"], DiscordTarget.FileNames([Picture()], Rating.Adult));
        Assert.Equal(["a.jpg"], DiscordTarget.FileNames([Picture()], Rating.General));
    }

    [Fact]
    public void Discord_payload_has_the_text_and_an_attachment_with_alt_text_per_picture()
    {
        var images = new[] { Picture("a.jpg", "a cat"), Picture("b.png") };

        var payload = DiscordTarget.BuildPayload("hi", images, ["SPOILER_a.jpg", "SPOILER_b.png"]);

        Assert.Equal("hi", payload["content"]!.GetValue<string>());
        var attachments = payload["attachments"]!.AsArray();
        Assert.Equal(2, attachments.Count);
        Assert.Equal(0, attachments[0]!["id"]!.GetValue<int>());
        Assert.Equal("SPOILER_a.jpg", attachments[0]!["filename"]!.GetValue<string>());
        Assert.Equal("a cat", attachments[0]!["description"]!.GetValue<string>());
        Assert.Null(attachments[1]!["description"]);
    }

    // ---- DeviantArt ---------------------------------------------------------------------------

    [Fact]
    public void DeviantArt_wants_a_short_title_and_numbers_it_per_picture()
    {
        var target = new DeviantArtTarget(new HttpClient());

        var untitled = target.Compose(Draft(title: ""), [Picture()]);
        var long_ = target.Compose(Draft(title: new string('x', 51)), [Picture()]);
        var three = target.Compose(Draft(), [Picture("1.jpg"), Picture("2.jpg"), Picture("3.jpg")]);

        Assert.Contains(untitled.Problems, p => p.Key == "scruff.problem.notitle");
        Assert.Contains(long_.Problems, p => p.Key == "scruff.problem.titlelong");
        Assert.True(three.CanGo);
        Assert.Contains(three.Notes, n => n.Key == "scruff.note.onebyone");

        Assert.Equal(["T"], DeviantArtTarget.Titles(Draft(), 1));
        Assert.Equal(["T (1/3)", "T (2/3)", "T (3/3)"], DeviantArtTarget.Titles(Draft(), 3));
    }

    [Fact]
    public void DeviantArt_tags_are_underscored_ascii_and_nothing_else()
    {
        Assert.Equal(["big_cat", "vore", "ct2"], DeviantArtTarget.CleanTags(["big cat", "#vore", "Big Cat", "cät2", "!!!"]));
    }

    [Fact]
    public void DeviantArt_rating_becomes_the_mature_level_and_noai_is_on()
    {
        var general = DeviantArtTarget.PublishForm("7", Rating.General, ["a"]);
        var mature = DeviantArtTarget.PublishForm("7", Rating.Mature, []);
        var adult = DeviantArtTarget.PublishForm("7", Rating.Adult, []);

        Assert.Contains(new KeyValuePair<string, string>("is_mature", "0"), general);
        Assert.Contains(new KeyValuePair<string, string>("tags[]", "a"), general);
        Assert.Contains(new KeyValuePair<string, string>("noai", "1"), general);
        Assert.DoesNotContain(general, p => p.Key == "mature_level");

        Assert.Contains(new KeyValuePair<string, string>("mature_level", "moderate"), mature);
        Assert.Contains(new KeyValuePair<string, string>("mature_classification[]", "nudity"), mature);
        Assert.Contains(new KeyValuePair<string, string>("mature_level", "strict"), adult);
        Assert.Contains(new KeyValuePair<string, string>("mature_classification[]", "sexual"), adult);
    }

    // ---- Tumblr -------------------------------------------------------------------------------

    [Fact]
    public void Tumblr_post_is_pictures_then_words_with_tags_as_written()
    {
        var post = TumblrTarget.BuildPost(Draft(tags: ["big cat", "a,b", "big cat"]), [Picture("a.jpg", "a cat"), Picture("b.jpg")]);

        var content = post["content"]!.AsArray();
        Assert.Equal(4, content.Count);
        Assert.Equal("image", content[0]!["type"]!.GetValue<string>());
        Assert.Equal("picture0", content[0]!["media"]![0]!["identifier"]!.GetValue<string>());
        Assert.Equal("image/jpeg", content[0]!["media"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("a cat", content[0]!["alt_text"]!.GetValue<string>());
        Assert.Null(content[1]!["alt_text"]);
        Assert.Equal("T", content[2]!["text"]!.GetValue<string>());
        Assert.Equal("heading2", content[2]!["subtype"]!.GetValue<string>());
        Assert.Equal("hello", content[3]!["text"]!.GetValue<string>());
        Assert.Equal("big cat,a b", post["tags"]!.GetValue<string>());
        Assert.Equal("published", post["state"]!.GetValue<string>());
        Assert.Null(post["has_community_label"]);
    }

    [Fact]
    public void Tumblr_rating_is_a_community_label_and_explicit_is_refused()
    {
        var target = new TumblrTarget(new HttpClient());

        var mature = TumblrTarget.BuildPost(Draft(rating: Rating.Mature), [Picture()]);
        Assert.True(mature["has_community_label"]!.GetValue<bool>());
        Assert.Equal("sexual_themes", mature["community_label_categories"]![0]!.GetValue<string>());

        var adult = target.Compose(Draft(rating: Rating.Adult), [Picture()]);
        Assert.Contains(adult.Problems, p => p.Key == "scruff.problem.noadult.tumblr");
        Assert.True(target.Compose(Draft(rating: Rating.Mature), [Picture()]).CanGo);
    }

    [Fact]
    public void Tumblr_blogs_are_read_primary_first_and_the_post_is_linked()
    {
        const string json = """
            {"meta":{"status":200},"response":{"user":{"name":"me","blogs":[
              {"name":"side","primary":false},{"name":"main","primary":true}]}}}
            """;

        var (name, blogs) = TumblrTarget.ParseUser(json);

        Assert.Equal("me", name);
        Assert.Equal(["main", "side"], blogs);
        Assert.Equal("123", TumblrTarget.ParsePostId("""{"response":{"id":123,"id_string":"123"}}"""));
        Assert.Equal("https://main.tumblr.com/post/123", TumblrTarget.PostLink("main", "123"));
        Assert.Null(TumblrTarget.ParsePostId("nonsense"));
    }

    [Fact]
    public async Task Nothing_posts_without_a_sign_in()
    {
        var da = new DeviantArtTarget(new HttpClient());
        var tumblr = new TumblrTarget(new HttpClient()) { Blog = "main" };
        var discord = new DiscordTarget(new HttpClient());

        Assert.False((await da.PostAsync(Draft(), [Picture()], CancellationToken.None)).Ok);
        Assert.False((await tumblr.PostAsync(Draft(), [Picture()], CancellationToken.None)).Ok);
        Assert.False((await discord.PostAsync(Draft(), [Picture()], CancellationToken.None)).Ok);
    }
}
