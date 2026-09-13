using System.Net.Http;
using Meows.Plugins.Birdwatch.Services;

namespace Meows.Tests;

/// <summary>
/// Birdwatch's three newer sources, each parsed from the shape its service actually answers
/// with, and the router that picks between the four by what was pasted.
/// </summary>
public sealed class FeedSourcesTests
{
    private static readonly FeedRouter Router = new(new HttpClient());

    [Theory]
    [InlineData("someone.bsky.social", "Bluesky", "someone.bsky.social")]
    [InlineData("@Someone.bsky.social", "Bluesky", "someone.bsky.social")]
    [InlineData("https://bsky.app/profile/someone.bsky.social/post/3abc", "Bluesky", "someone.bsky.social")]
    [InlineData("did:plc:abc123", "Bluesky", "did:plc:abc123")]
    [InlineData("@artist@mastodon.social", "Mastodon", "artist@mastodon.social")]
    [InlineData("artist@pixelfed.social", "Mastodon", "artist@pixelfed.social")]
    [InlineData("https://mastodon.art/@Artist", "Mastodon", "artist@mastodon.art")]
    [InlineData("https://mastodon.art/@artist/112233", "Mastodon", "artist@mastodon.art")]
    [InlineData("https://social.example/users/artist", "Mastodon", "artist@social.example")]
    [InlineData("u/someone", "Reddit", "u/someone")]
    [InlineData("r/ImaginaryCats", "Reddit", "r/ImaginaryCats")]
    [InlineData("https://www.reddit.com/user/someone/", "Reddit", "u/someone")]
    [InlineData("https://old.reddit.com/r/ImaginaryCats/comments/abc/title/", "Reddit", "r/ImaginaryCats")]
    [InlineData("https://example.tumblr.com/rss", "Feed", "https://example.tumblr.com/rss")]
    [InlineData("https://backend.deviantart.com/rss.xml?q=gallery:someone", "Feed", "https://backend.deviantart.com/rss.xml?q=gallery:someone")]
    public void What_was_pasted_goes_to_the_service_that_owns_that_shape(string pasted, string service, string kept)
    {
        Assert.Equal(service, Router.ServiceOf(pasted));
        Assert.Equal(kept, Router.TidyHandle(pasted));

        // The kept handle routes the same way after a restart.
        Assert.Equal(service, Router.ServiceOf(kept));
    }

    [Fact]
    public void Mastodon_statuses_become_posts_with_their_attachments_and_boosts_marked()
    {
        const string json = """
            [
              {
                "id": "200", "uri": "https://mastodon.art/users/artist/statuses/200", "url": "https://mastodon.art/@artist/200",
                "created_at": "2026-09-01T10:00:00.000Z", "sensitive": true, "spoiler_text": "eye contact",
                "content": "<p>New piece! <a href=\"#\">#art</a></p><p>Second line &amp; more</p>",
                "account": { "acct": "artist", "display_name": "The Artist" },
                "media_attachments": [
                  { "type": "image", "url": "https://files.mastodon.art/a.png", "preview_url": "https://files.mastodon.art/a-small.png", "description": "a cat" },
                  { "type": "video", "url": "https://files.mastodon.art/b.mp4", "preview_url": "https://files.mastodon.art/b.jpg" }
                ],
                "reblog": null
              },
              {
                "id": "199", "uri": "https://mastodon.art/users/artist/statuses/199", "created_at": "2026-08-30T10:00:00.000Z",
                "account": { "acct": "artist", "display_name": "The Artist" },
                "media_attachments": [],
                "reblog": {
                  "uri": "https://other.example/users/friend/statuses/7", "url": "https://other.example/@friend/7",
                  "content": "<p>boosted</p>", "sensitive": false, "spoiler_text": "",
                  "account": { "acct": "friend@other.example", "display_name": "Friend" },
                  "media_attachments": [ { "type": "image", "url": "https://other.example/c.jpg", "preview_url": "https://other.example/c-s.jpg" } ]
                }
              }
            ]
            """;

        var page = MastodonFeed.Parse(json, "mastodon.art");

        Assert.Equal(2, page.Posts.Count);
        Assert.Equal("199", page.Cursor);

        var own = page.Posts[0];
        Assert.Equal("artist@mastodon.art", own.AuthorHandle);
        Assert.Equal("The Artist", own.AuthorName);
        Assert.Equal("New piece! #art\nSecond line & more", own.Text);
        Assert.Equal(["sensitive", "eye contact"], own.Labels);
        Assert.False(own.IsRepost);
        Assert.Equal(2, own.Media.Count);
        Assert.Equal("https://files.mastodon.art/a.png", own.Media[0].FullUrl);
        Assert.Equal("a cat", own.Media[0].Alt);
        Assert.Equal(MediaKind.Video, own.Media[1].Kind);
        Assert.False(own.Media[1].CanSave);

        var boost = page.Posts[1];
        Assert.True(boost.IsRepost);
        Assert.Equal("https://other.example/users/friend/statuses/7", boost.Id);
        Assert.Single(boost.Media);
        Assert.Equal("https://other.example/@friend/7", boost.WebUrl);
    }

    [Fact]
    public void An_empty_mastodon_page_has_no_next_page()
    {
        var page = MastodonFeed.Parse("[]", "mastodon.art");

        Assert.Empty(page.Posts);
        Assert.Null(page.Cursor);
    }

    [Fact]
    public void Reddit_listings_become_posts_with_images_galleries_and_video_told_apart()
    {
        const string json = """
            {
              "data": {
                "after": "t3_next",
                "children": [
                  { "data": { "id": "img1", "author": "someone", "title": "A cat", "created_utc": 1756720000, "permalink": "/r/cats/comments/img1/a_cat/",
                              "post_hint": "image", "url_overridden_by_dest": "https://i.redd.it/abc.jpg", "over_18": false,
                              "preview": { "images": [ { "source": { "url": "https://preview.redd.it/abc.jpg?auto=webp" },
                                                         "resolutions": [ { "url": "https://preview.redd.it/abc.jpg?width=108" }, { "url": "https://preview.redd.it/abc.jpg?width=216" }, { "url": "https://preview.redd.it/abc.jpg?width=320" } ] } ] } } },
                  { "data": { "id": "gal1", "author": "someone", "title": "Two cats", "created_utc": 1756720001, "permalink": "/r/cats/comments/gal1/two/",
                              "is_gallery": true, "over_18": true,
                              "media_metadata": { "x1": { "s": { "u": "https://i.redd.it/x1.jpg" }, "p": [ { "u": "https://preview.redd.it/x1.jpg?width=108" } ] },
                                                  "x2": { "s": { "u": "https://i.redd.it/x2.png" } } } } },
                  { "data": { "id": "vid1", "author": "someone", "title": "Cat video", "created_utc": 1756720002, "permalink": "/r/cats/comments/vid1/v/",
                              "is_video": true, "thumbnail": "https://b.thumbs.redditmedia.com/v.jpg", "url": "https://v.redd.it/xyz" } },
                  { "data": { "id": "txt1", "author": "someone", "title": "Just words", "created_utc": 1756720003, "permalink": "/r/cats/comments/txt1/w/", "url": "https://www.reddit.com/r/cats/comments/txt1/w/" } }
                ]
              }
            }
            """;

        var page = RedditFeed.Parse(json);

        Assert.Equal(4, page.Posts.Count);
        Assert.Equal("t3_next", page.Cursor);

        var image = page.Posts[0];
        Assert.Equal("t3_img1", image.Id);
        Assert.Equal("u/someone", image.AuthorHandle);
        Assert.Equal("https://www.reddit.com/r/cats/comments/img1/a_cat/", image.WebUrl);
        Assert.Equal("https://i.redd.it/abc.jpg", Assert.Single(image.Media).FullUrl);
        Assert.Equal("https://preview.redd.it/abc.jpg?width=320", image.Media[0].ThumbnailUrl);
        Assert.Equal(new DateTimeOffset(2025, 9, 1, 9, 46, 40, TimeSpan.Zero), image.PostedAt);

        var gallery = page.Posts[1];
        Assert.Equal(2, gallery.Media.Count);
        Assert.Contains("nsfw", gallery.Labels);
        Assert.Equal("https://preview.redd.it/x1.jpg?width=108", gallery.Media[0].ThumbnailUrl);

        var video = page.Posts[2];
        Assert.Equal(MediaKind.Video, Assert.Single(video.Media).Kind);
        Assert.False(video.HasSaveable);

        Assert.Empty(page.Posts[3].Media);
    }

    [Fact]
    public void An_rss_feed_yields_pictures_from_enclosures_media_tags_and_the_description()
    {
        const string xml = """
            <?xml version="1.0"?>
            <rss version="2.0" xmlns:media="http://search.yahoo.com/mrss/" xmlns:dc="http://purl.org/dc/elements/1.1/">
              <channel>
                <title>Some Blog</title>
                <item>
                  <title>Enclosed</title><link>https://blog.example/1</link><guid>tag:1</guid>
                  <pubDate>Tue, 01 Sep 2026 10:00:00 GMT</pubDate><dc:creator>author</dc:creator>
                  <enclosure url="https://cdn.example/one.jpg" type="image/jpeg" />
                </item>
                <item>
                  <title>Media</title><link>https://blog.example/2</link>
                  <media:content url="https://cdn.example/two.png" medium="image"><media:thumbnail url="https://cdn.example/two-s.png"/></media:content>
                </item>
                <item>
                  <title>In the body</title><link>https://blog.example/3</link>
                  <description><![CDATA[<p>Look</p><img src="https://cdn.example/pixel.gif" width="1" height="1"><img src="https://cdn.example/three.webp" alt="x"><img src="https://cdn.example/three.webp">]]></description>
                </item>
                <item>
                  <title>Words only</title><link>https://blog.example/4</link><description>nothing here</description>
                </item>
              </channel>
            </rss>
            """;

        var page = RssFeed.Parse(xml, "https://blog.example/rss");

        Assert.Equal(4, page.Posts.Count);
        Assert.Null(page.Cursor);
        Assert.Equal("author", page.Posts[0].AuthorHandle);
        Assert.Equal("Some Blog", page.Posts[0].AuthorName);
        Assert.Equal("tag:1", page.Posts[0].Id);
        Assert.Equal("https://cdn.example/one.jpg", Assert.Single(page.Posts[0].Media).FullUrl);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero), page.Posts[0].PostedAt);

        Assert.Equal("https://cdn.example/two-s.png", Assert.Single(page.Posts[1].Media).ThumbnailUrl);
        Assert.Equal("Some Blog", page.Posts[1].AuthorHandle);

        // The tracking pixel is dropped, the real picture is taken once.
        Assert.Equal("https://cdn.example/three.webp", Assert.Single(page.Posts[2].Media).FullUrl);

        Assert.Empty(page.Posts[3].Media);
    }

    [Fact]
    public void An_atom_feed_is_read_the_same_way()
    {
        const string xml = """
            <?xml version="1.0"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <title>Atom Site</title>
              <entry>
                <id>urn:1</id><title>First</title><published>2026-09-02T12:00:00Z</published>
                <author><name>writer</name></author>
                <link rel="alternate" href="https://site.example/first"/>
                <link rel="enclosure" type="image/png" href="https://site.example/first.png"/>
              </entry>
              <entry>
                <id>urn:2</id><title>Second</title>
                <content type="html">&lt;img src="https://site.example/second.jpg"&gt;</content>
              </entry>
            </feed>
            """;

        var page = RssFeed.Parse(xml, "https://site.example/feed");

        Assert.Equal(2, page.Posts.Count);
        Assert.Equal("writer", page.Posts[0].AuthorHandle);
        Assert.Equal("https://site.example/first", page.Posts[0].WebUrl);
        Assert.Equal("https://site.example/first.png", Assert.Single(page.Posts[0].Media).FullUrl);
        Assert.Equal("https://site.example/second.jpg", Assert.Single(page.Posts[1].Media).FullUrl);
    }

    [Fact]
    public void Something_that_is_not_a_feed_is_said_to_be_not_a_feed()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => RssFeed.Parse("<html><body>no</body></html>", "https://x"));
        Assert.Empty(RssFeed.Parse("<rss><channel><title>t</title></channel></rss>", "https://x").Posts);
        Assert.Contains("feed", ex.Message);
    }

    [Fact]
    public void Html_is_stripped_to_its_words()
    {
        Assert.Equal("one\ntwo & three", MastodonFeed.StripHtml("<p>one</p><p>two &amp; <b>three</b></p>"));
    }
}
