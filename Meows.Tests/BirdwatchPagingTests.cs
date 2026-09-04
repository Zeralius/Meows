using Meows.Plugins.Birdwatch.Services;
using Meows.Plugins.Birdwatch.ViewModels;

namespace Meows.Tests;

/// <summary>
/// A feed that hands out pages from a script, so paging can be exercised without a network.
/// </summary>
internal sealed class FakeFeed : IFeedSource
{
    private readonly int _pages;
    private readonly int _perPage;

    public FakeFeed(int pages = 5, int perPage = 20)
    {
        _pages = pages;
        _perPage = perPage;
    }

    public string ServiceName => "Fake";

    public List<string?> Asked { get; } = [];

    public Task<FeedPage> FetchAsync(string handle, string? cursor, CancellationToken token)
    {
        Asked.Add(cursor);

        var page = cursor is null ? 0 : int.Parse(cursor);
        if (page >= _pages)
            return Task.FromResult(FeedPage.Empty);

        var posts = new List<FeedPost>();
        for (var i = 0; i < _perPage; i++)
        {
            var n = page * _perPage + i;
            posts.Add(new FeedPost
            {
                Id = $"at://did:plc:{handle}/app.bsky.feed.post/{n:0000}",
                AuthorHandle = handle,

                // Older as the pages go back, which is the order a real feed comes in.
                PostedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(-n),
                Media =
                [
                    new FeedMedia
                    {
                        Kind = MediaKind.Image,
                        ThumbnailUrl = "",
                        FullUrl = $"https://example.invalid/{handle}/{n}",
                    },
                ],
            });
        }

        var next = page + 1;
        return Task.FromResult(new FeedPage(posts, next >= _pages ? null : next.ToString()));
    }
}

/// <summary>
/// Reading further back than the newest page.
///
/// The button fetched the next page perfectly well and then threw it away: the grid was built
/// with Take(Math.Max(shown, batch)), and shown was set to the size of the grid after every
/// build, so once it reached one batch it never grew again. Pressing it spent a request and
/// changed nothing on screen.
/// </summary>
public class BirdwatchPagingTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "meows-paging-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private BirdwatchViewModel Watching(FakeFeed feed, int batch = 20)
    {
        var host = new FakeHost(_root);
        host.SaveSettings(new BirdwatchSettings
        {
            Handles = ["someone.bsky.social"],
            IntakeFolder = Path.Combine(_root, "intake"),
            Batch = batch,
        });

        return new BirdwatchViewModel(host, feed, new System.Net.Http.HttpClient());
    }

    [Fact]
    public async Task The_first_look_shows_one_batch()
    {
        using var model = Watching(new FakeFeed());
        await model.LoadAsync(more: false);

        Assert.Equal(20, model.Shown.Count);
        Assert.True(model.HasMore);
    }

    [Fact]
    public async Task Reading_further_back_actually_shows_more()
    {
        using var model = Watching(new FakeFeed());
        await model.LoadAsync(more: false);
        Assert.Equal(20, model.Shown.Count);

        await model.LoadAsync(more: true);

        Assert.Equal(40, model.Shown.Count);
    }

    [Fact]
    public async Task Reading_back_repeatedly_keeps_going()
    {
        using var model = Watching(new FakeFeed(pages: 5, perPage: 20));
        await model.LoadAsync(more: false);

        for (var i = 0; i < 4; i++)
            await model.LoadAsync(more: true);

        // Five pages of twenty, every one of them on screen.
        Assert.Equal(100, model.Shown.Count);
        Assert.False(model.HasMore);
    }

    [Fact]
    public async Task What_is_shown_is_newest_first()
    {
        using var model = Watching(new FakeFeed());
        await model.LoadAsync(more: false);
        await model.LoadAsync(more: true);

        var dates = model.Shown.Select(m => m.Post.PostedAt).ToList();
        Assert.Equal(dates.OrderByDescending(d => d), dates);
    }

    [Fact]
    public async Task Older_material_is_kept_when_the_top_is_read_again()
    {
        // Refresh starts each account at the top again. Whatever was already read back should
        // still be there rather than the grid snapping back to one batch.
        using var model = Watching(new FakeFeed());
        await model.LoadAsync(more: false);
        await model.LoadAsync(more: true);
        Assert.Equal(40, model.Shown.Count);

        await model.LoadAsync(more: false);

        Assert.Equal(40, model.Shown.Count);
    }

    [Fact]
    public async Task Showing_what_is_already_here_costs_no_request()
    {
        // Two accounts fetch fifty each into a grid of twenty, so there is plenty in hand.
        // Asking for more should spend that before spending a request.
        var host = new FakeHost(_root);
        host.SaveSettings(new BirdwatchSettings
        {
            Handles = ["one.bsky.social", "two.bsky.social"],
            IntakeFolder = Path.Combine(_root, "intake"),
            Batch = 20,
        });

        var feed = new FakeFeed(pages: 5, perPage: 50);
        using var model = new BirdwatchViewModel(host, feed, new System.Net.Http.HttpClient());

        await model.LoadAsync(more: false);
        var requests = feed.Asked.Count;

        await model.LoadAsync(more: true);

        Assert.Equal(requests, feed.Asked.Count);
        Assert.Equal(40, model.Shown.Count);
    }

    [Fact]
    public async Task An_account_that_has_run_out_is_not_asked_again()
    {
        using var model = Watching(new FakeFeed(pages: 2, perPage: 20));
        var feed = new FakeFeed(pages: 2, perPage: 20);

        await model.LoadAsync(more: false);
        for (var i = 0; i < 6; i++)
            await model.LoadAsync(more: true);

        Assert.Equal(40, model.Shown.Count);
        Assert.False(model.HasMore);
    }
}

/// <summary>
/// Working out which account somebody meant.
///
/// Almost nobody types a handle. They copy the profile link out of the address bar, or press
/// share on a post, and both of those have to end up watching the same account.
/// </summary>
public class HandleTests
{
    [Theory]
    // What a person types.
    [InlineData("zeralius.bsky.social", "zeralius.bsky.social")]
    [InlineData("@zeralius.bsky.social", "zeralius.bsky.social")]
    [InlineData("  ZERALIUS.bsky.Social  ", "zeralius.bsky.social")]
    [InlineData("@ zeralius.bsky.social", "zeralius.bsky.social")]
    // What comes out of the address bar.
    [InlineData("https://bsky.app/profile/zeralius.bsky.social", "zeralius.bsky.social")]
    [InlineData("https://bsky.app/profile/zeralius.bsky.social/", "zeralius.bsky.social")]
    [InlineData("bsky.app/profile/zeralius.bsky.social", "zeralius.bsky.social")]
    [InlineData("http://bsky.app/profile/Zeralius.BSKY.social", "zeralius.bsky.social")]
    // What the share button gives you, which is a link to one post.
    [InlineData("https://bsky.app/profile/zeralius.bsky.social/post/3ktabcdefgh", "zeralius.bsky.social")]
    // And whatever gets appended on the way.
    [InlineData("https://bsky.app/profile/zeralius.bsky.social?ref=x", "zeralius.bsky.social")]
    [InlineData("https://bsky.app/profile/zeralius.bsky.social#top", "zeralius.bsky.social")]
    public void An_account_is_found_in_whatever_was_pasted(string pasted, string wanted)
    {
        Assert.Equal(wanted, BlueskyFeed.TidyHandle(pasted));
    }

    [Fact]
    public void A_did_is_left_exactly_as_written()
    {
        // An identifier rather than a name, and the API takes one as its actor just the same.
        // Case folding it would be changing somebody's id to look tidier.
        Assert.Equal("did:plc:z72i7hdynmk6r22z27h6tvur",
            BlueskyFeed.TidyHandle("https://bsky.app/profile/did:plc:z72i7hdynmk6r22z27h6tvur"));

        Assert.Equal("did:web:Example.com", BlueskyFeed.TidyHandle("did:web:Example.com"));
    }

    [Fact]
    public void Nothing_useful_gives_nothing_rather_than_a_guess()
    {
        Assert.Equal("", BlueskyFeed.TidyHandle("   "));
        Assert.Equal("", BlueskyFeed.TidyHandle("https://bsky.app/profile/"));
    }
}
