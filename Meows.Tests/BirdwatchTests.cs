using Meows.Plugins.Birdwatch.Services;

namespace Meows.Tests;

/// <summary>
/// Reading a Bluesky feed.
///
/// Everything here runs against a real getAuthorFeed response captured from the live service,
/// trimmed to one post of each shape. A fixture written by hand to match the parser would only
/// ever prove the parser agrees with itself, and the shapes are the entire difficulty: the
/// difference between an image post, a quoted image post and a video is where all the bugs live.
/// </summary>
public class BlueskyFeedTests
{
    private static string Captured()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "bluesky-author-feed.json");
        Assert.True(File.Exists(path), path);
        return File.ReadAllText(path);
    }

    private static FeedPage Page() => BlueskyFeed.Parse(Captured());

    [Fact]
    public void The_captured_feed_reads()
    {
        var page = Page();

        Assert.Equal(6, page.Posts.Count);
        Assert.NotNull(page.Cursor);
    }

    [Fact]
    public void A_post_carries_who_wrote_it_and_when()
    {
        var post = Page().Posts.First();

        Assert.Equal("bsky.app", post.AuthorHandle);
        Assert.NotEmpty(post.AuthorName);
        Assert.NotEqual(DateTimeOffset.MinValue, post.PostedAt);
        Assert.StartsWith("at://", post.Id);
    }

    [Fact]
    public void Images_come_through_with_something_to_save()
    {
        // Two posts carry images: one posts them directly, the other quotes a post that does.
        var withImages = Page().Posts.Where(p => p.Media.Any(m => m.Kind == MediaKind.Image)).ToList();
        Assert.Equal(2, withImages.Count);

        var image = withImages[0].Media.First(m => m.Kind == MediaKind.Image);

        Assert.True(image.CanSave);
        Assert.StartsWith("https://cdn.bsky.app/img/feed_fullsize/", image.FullUrl);
        Assert.StartsWith("https://cdn.bsky.app/img/feed_thumbnail/", image.ThumbnailUrl);
        Assert.NotEmpty(image.Alt);
    }

    [Fact]
    public void A_video_is_shown_but_not_offered_as_a_download()
    {
        // The API gives an HLS manifest rather than a file. Offering a button that cannot work
        // is worse than not offering one.
        var videos = Page().Posts
            .SelectMany(p => p.Media)
            .Where(m => m.Kind == MediaKind.Video)
            .ToList();

        Assert.NotEmpty(videos);
        Assert.All(videos, v =>
        {
            Assert.False(v.CanSave);
            Assert.Null(v.FullUrl);
            Assert.NotEmpty(v.ThumbnailUrl);
        });
    }

    [Fact]
    public void A_quoted_post_hands_over_its_media()
    {
        // Quoting an image post is how a lot of art travels, and the pictures are just as
        // fetchable from there. This one is a repost of a quote of an image post, which is
        // three levels of indirection between the feed and the picture.
        var quote = Page().Posts.Single(p => p.Id.EndsWith("3mtucjsd5lc2o", StringComparison.Ordinal));

        var image = Assert.Single(quote.Media);
        Assert.Equal(MediaKind.Image, image.Kind);
        Assert.True(image.CanSave);
    }

    [Fact]
    public void A_quote_of_a_quote_is_left_alone()
    {
        // One level only. Anything past that is somebody else's thread rather than the media
        // the account being watched chose to show.
        var page = BlueskyFeed.Parse("""
            { "feed": [ { "post": { "uri": "at://x/y/z", "embed": {
                "$type": "app.bsky.embed.record#view",
                "record": { "embeds": [ {
                    "$type": "app.bsky.embed.record#view",
                    "record": { "embeds": [ {
                        "$type": "app.bsky.embed.images#view",
                        "images": [ { "thumb": "t", "fullsize": "f" } ]
                    } ] }
                } ] }
            } } } ] }
            """);

        Assert.Empty(Assert.Single(page.Posts).Media);
    }

    [Fact]
    public void A_link_preview_is_not_treated_as_the_posters_media()
    {
        // The thumbnail on a link card belongs to the site being linked, not to whoever posted
        // the link, so there is nothing here to hand out.
        var external = Page().Posts.Single(p =>
            p.Text.StartsWith("We've enhanced Bluesky link cards", StringComparison.OrdinalIgnoreCase));

        Assert.Empty(external.Media);
    }

    [Fact]
    public void A_repost_says_that_it_is_one()
    {
        var reposts = Page().Posts.Where(p => p.IsRepost).ToList();

        Assert.NotEmpty(reposts);
    }

    [Fact]
    public void A_post_links_back_to_where_it_came_from()
    {
        var post = Page().Posts.First();

        Assert.StartsWith("https://bsky.app/profile/bsky.app/post/", post.WebUrl);
    }

    [Fact]
    public void Nothing_in_the_feed_throws_on_a_shape_it_has_not_seen()
    {
        // Every field is read defensively, because the service adds embed types on its own
        // schedule and a new one must be ignored rather than fatal.
        var page = BlueskyFeed.Parse("""
            { "feed": [
                { "post": {} },
                { "post": { "embed": { "$type": "app.bsky.embed.somethingNew#view" } } },
                { "nothing": true }
            ] }
            """);

        Assert.Equal(2, page.Posts.Count);
        Assert.All(page.Posts, p => Assert.Empty(p.Media));
        Assert.Null(page.Cursor);
    }

    [Fact]
    public void An_empty_response_is_an_empty_page()
    {
        Assert.Empty(BlueskyFeed.Parse("{}").Posts);
        Assert.Empty(BlueskyFeed.Parse("""{ "feed": [] }""").Posts);
    }

    [Theory]
    [InlineData("@bsky.app", "bsky.app")]
    [InlineData("  BSKY.App  ", "bsky.app")]
    [InlineData("@ bsky.app", "bsky.app")]
    [InlineData("bsky.app", "bsky.app")]
    public void A_handle_is_tidied_the_way_people_actually_paste_them(string typed, string wanted)
    {
        Assert.Equal(wanted, BlueskyFeed.TidyHandle(typed));
    }

    [Fact]
    public void A_post_with_no_handle_gets_no_link_rather_than_a_broken_one()
    {
        Assert.Null(BlueskyFeed.WebLink(null, "at://did/app.bsky.feed.post/abc"));
        Assert.Null(BlueskyFeed.WebLink("someone.bsky.social", ""));
    }
}

/// <summary>
/// Saving a picture into the intake folder, which is where Kibble picks it up.
/// </summary>
public class MediaSaverTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "meows-birdwatch-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private static FeedPost Post(string handle = "artist.bsky.social") => new()
    {
        Id = "at://did:plc:abc/app.bsky.feed.post/3ktabcdefgh",
        AuthorHandle = handle,
        AuthorName = "An Artist",
        PostedAt = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero),
    };

    /// <summary>
    /// The type has to come from the response, not the address.
    ///
    /// Bluesky's CDN serves a URL ending in a bare content hash and hands back WebP. Guessing
    /// .jpg from the look of the link would write WebP bytes into a file called .jpg, and the
    /// bot would then post a file whose name disagrees with itself.
    /// </summary>
    [Theory]
    [InlineData("image/webp", ".webp")]
    [InlineData("image/jpeg", ".jpg")]
    [InlineData("IMAGE/PNG", ".png")]
    [InlineData("image/gif", ".gif")]
    [InlineData(null, ".bin")]
    [InlineData("application/octet-stream", ".bin")]
    public void The_extension_comes_from_the_content_type(string? contentType, string wanted)
    {
        Assert.Equal(wanted, MediaSaver.ExtensionFor(contentType));
    }

    [Fact]
    public void Every_type_it_names_is_one_the_bot_will_post()
    {
        // A saved file that Kibble refuses is a file that sits in the intake folder forever.
        foreach (var type in new[] { "image/webp", "image/jpeg", "image/png", "image/gif", "image/bmp" })
        {
            var extension = MediaSaver.ExtensionFor(type);
            Assert.True(Meows.Bot.MediaRules.IsPostable("x" + extension), extension);
        }
    }

    [Fact]
    public void The_name_says_who_posted_it_and_when()
    {
        // A folder of content hashes is unreadable a month later. This is the whole reason the
        // CDN's own name is thrown away.
        var name = MediaSaver.NameFor(Post(), 0, ".webp");

        Assert.Equal("artist.bsky.social_2026-09-04_3ktabcdefgh.webp", name);
    }

    [Fact]
    public void Two_pictures_from_one_post_do_not_collide()
    {
        Assert.NotEqual(MediaSaver.NameFor(Post(), 0, ".webp"), MediaSaver.NameFor(Post(), 1, ".webp"));
    }

    [Fact]
    public void A_handle_that_would_not_survive_a_file_name_is_cleaned()
    {
        var name = MediaSaver.NameFor(Post("bad:/name*"), 0, ".jpg");

        Assert.DoesNotContain(':', name);
        Assert.DoesNotContain('*', name);
        Assert.Equal(-1, name.IndexOfAny(Path.GetInvalidFileNameChars()));
    }

    [Fact]
    public async Task A_video_is_refused_rather_than_written_empty()
    {
        var saver = new MediaSaver(new System.Net.Http.HttpClient());
        var video = new FeedMedia { Kind = MediaKind.Video, ThumbnailUrl = "x", FullUrl = null };

        var result = await saver.SaveAsync(Post(), video, 0, _folder, CancellationToken.None);

        Assert.Equal(SaveOutcome.NotSaveable, result.Outcome);
        Assert.False(Directory.Exists(_folder) && Directory.GetFiles(_folder).Length > 0);
    }

    [Fact]
    public async Task Something_already_saved_is_not_fetched_again()
    {
        Directory.CreateDirectory(_folder);
        var existing = Path.Combine(_folder, MediaSaver.NameFor(Post(), 0, ".webp"));
        File.WriteAllText(existing, "already here");

        // The handler throws if it is used at all, which is the point: the name comes from the
        // post rather than the response, so this is answered before spending a download.
        var saver = new MediaSaver(new System.Net.Http.HttpClient(new ThrowingHandler()));
        var image = new FeedMedia
        {
            Kind = MediaKind.Image,
            ThumbnailUrl = "t",
            FullUrl = "https://cdn.bsky.app/img/feed_fullsize/plain/did/abc",
        };

        var result = await saver.SaveAsync(Post(), image, 0, _folder, CancellationToken.None);

        Assert.Equal(SaveOutcome.AlreadyThere, result.Outcome);
        Assert.Equal("already here", File.ReadAllText(existing));
    }

    [Fact]
    public void Already_saved_recognises_the_file_whatever_it_was_saved_as()
    {
        // The extension is not known until the response arrives, so the question has to be
        // asked about the name without one.
        Directory.CreateDirectory(_folder);
        Assert.Null(MediaSaver.AlreadySaved(_folder, Post(), 0));

        File.WriteAllText(Path.Combine(_folder, MediaSaver.BaseNameFor(Post(), 0) + ".png"), "x");
        Assert.NotNull(MediaSaver.AlreadySaved(_folder, Post(), 0));
    }

    [Fact]
    public void A_half_finished_download_does_not_count_as_saved()
    {
        // A .part is what a cancelled or failed fetch leaves. Counting it would mean the
        // picture is never retried.
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, MediaSaver.BaseNameFor(Post(), 0) + ".webp.part"), "half");

        Assert.Null(MediaSaver.AlreadySaved(_folder, Post(), 0));
    }

    private sealed class ThrowingHandler : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The network should not have been touched.");
    }
}
