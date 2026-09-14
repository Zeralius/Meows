using System.Net;
using System.Net.Http;
using Meows.Plugins.Birdwatch.Services;
using Meows.Plugins.Birdwatch.ViewModels;

namespace Meows.Tests;

/// <summary>
/// Birdwatch since 2.23.0: an account can be paused and stays in the list, the same picture from
/// two accounts is saved once because the shared seen table is consulted, and an account can be
/// tried before it is watched.
/// </summary>
public sealed class BirdwatchSeenTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "birdseen-" + Guid.NewGuid().ToString("N")[..10]);

    /// <summary>Answers every picture URL with the same bytes, which is what two accounts posting one picture looks like.</summary>
    private sealed class SameBytes : HttpMessageHandler
    {
        public int Fetched { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Fetched++;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(TestPictures.Jpeg()),
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            return Task.FromResult(response);
        }
    }

    private (FakeHost Host, BirdwatchViewModel Model, FakeFeed Feed, SameBytes Web) Open(params string[] handles)
    {
        var host = new FakeHost(_root);
        host.SaveSettings(new BirdwatchSettings { Handles = handles.ToList(), IntakeFolder = Path.Combine(_root, "intake") });
        var feed = new FakeFeed(pages: 1, perPage: 2);
        var web = new SameBytes();
        return (host, new BirdwatchViewModel(host, feed, new HttpClient(web)), feed, web);
    }

    [Fact]
    public async Task A_paused_account_stays_in_the_list_and_is_skipped_until_unpaused()
    {
        var (host, model, feed, _) = Open("one", "two");
        using var _ = model;

        model.Watched.First(w => w.Handle == "two").IsPaused = true;
        await model.LoadAsync(more: false);

        Assert.Equal(2, model.Watched.Count);
        Assert.Single(feed.Asked);
        Assert.All(model.Shown, m => Assert.Equal("one", m.AuthorHandle));
        Assert.Equal("paused", model.Watched.First(w => w.Handle == "two").Status);
        Assert.Contains("two", host.LoadSettings<BirdwatchSettings>()!.Paused);

        // Unpausing reads it on the next look, and the setting follows.
        model.Watched.First(w => w.Handle == "two").IsPaused = false;
        await model.LoadAsync(more: false);
        Assert.Contains(model.Shown, m => m.AuthorHandle == "two");
        Assert.Empty(host.LoadSettings<BirdwatchSettings>()!.Paused);
    }

    [Fact]
    public async Task The_same_picture_from_two_accounts_is_saved_once_and_the_second_is_dropped()
    {
        var (host, model, _, web) = Open("one", "two");
        using var _ = model;
        await model.LoadAsync(more: false);

        var first = model.Shown.First(m => m.AuthorHandle == "one");
        var second = model.Shown.First(m => m.AuthorHandle == "two");

        await model.SaveAsync(first);
        await model.SaveAsync(second);

        var intake = Path.Combine(_root, "intake");
        Assert.Single(Directory.GetFiles(intake));
        Assert.True(first.IsSaved);
        // Dropped, and marked as such so it is not offered again; nothing half-written left behind.
        Assert.True(second.IsSaved);
        Assert.Empty(Directory.GetFiles(intake, "*.part"));
        Assert.Contains("already came in", model.Status);
        Assert.Equal(2, web.Fetched);
        // The hash is in the shared table, so Kibble or Saucer would know it too.
        Assert.NotNull(host.Store.Seen(Meows.Disk.ContentHash.Full(Directory.GetFiles(intake)[0])!));
    }

    [Fact]
    public async Task Trying_a_handle_reads_it_and_says_what_it_would_save_without_watching_it()
    {
        var (host, model, feed, _) = Open();
        using var _ = model;

        model.NewHandle = "maybe";
        await model.TryHandleAsync();

        Assert.Empty(model.Watched);
        Assert.Empty(host.LoadSettings<BirdwatchSettings>()!.Handles);
        Assert.Single(feed.Asked);
        Assert.Contains("@maybe on Fake: 2 posts", model.TrialText);
        Assert.Contains("2 pictures", model.TrialText);

        // Watching it afterwards clears the trial line.
        model.AddHandleCommand.Execute(null);
        Assert.Single(model.Watched);
        Assert.False(model.HasTrial);
    }

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
}
