using Meows.Plugins.Abstractions;
using Meows.Plugins.Chonk.Services;
using Meows.Plugins.Chonk.ViewModels;
using Meows.Plugins.Kibble.ViewModels;

namespace Meows.Tests;

/// <summary>
/// Ctrl+K reaching into what a plugin is showing. The contract is small: match every word
/// somewhere, hand back a title, a detail and a way to land on the thing.
/// </summary>
public sealed class SearchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "search-" + Guid.NewGuid().ToString("N")[..10]);

    public SearchTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Every_word_has_to_appear_in_one_of_the_texts()
    {
        var words = SearchWords.Split("  cat   Photo ");

        Assert.Equal(["cat", "Photo"], words);
        Assert.True(SearchWords.Match(words, "photo of a CAT.jpg"));
        Assert.True(SearchWords.Match(words, "cat", "photo"));
        Assert.False(SearchWords.Match(words, "cat", null));
        Assert.True(SearchWords.Match([], "anything"));
    }

    [Fact]
    public void Every_shipped_plugin_view_model_can_be_searched()
    {
        // The palette only reaches into plugins that say so. One that stops saying so drops out
        // of Ctrl+K silently, which is what this is here to catch.
        foreach (var type in new[]
                 {
                     typeof(Plugins.Purrge.ViewModels.PurrgeViewModel), typeof(ChonkViewModel), typeof(KibbleViewModel),
                     typeof(Plugins.Litter.ViewModels.LitterViewModel), typeof(Plugins.Molt.ViewModels.MoltViewModel),
                     typeof(Plugins.Mouser.ViewModels.MouserViewModel), typeof(Plugins.Saucer.ViewModels.SaucerViewModel),
                     typeof(Plugins.Birdwatch.ViewModels.BirdwatchViewModel), typeof(Plugins.Tin.ViewModels.TinViewModel),
                     typeof(Plugins.Collar.ViewModels.CollarViewModel), typeof(Plugins.Scruff.ViewModels.ScruffViewModel),
                     typeof(Plugins.Perch.ViewModels.PerchViewModel), typeof(Plugins.Portion.ViewModels.PortionViewModel),
                     typeof(Plugins.TelegramPoster.ViewModels.TelegramPosterViewModel),
                 })
        {
            Assert.True(typeof(ISearchable).IsAssignableFrom(type), $"{type.Name} is not searchable");
        }
    }

    [Fact]
    public void Chonk_finds_anything_in_the_scan_and_lands_on_it_where_it_lives()
    {
        Directory.CreateDirectory(Path.Combine(_root, "games", "deep"));
        File.WriteAllBytes(Path.Combine(_root, "games", "deep", "shader_cache.bin"), new byte[4096]);
        File.WriteAllBytes(Path.Combine(_root, "games", "other.bin"), new byte[2048]);

        using var model = new ChonkViewModel(new FakeHost(Path.Combine(_root, "hostdata")));
        model.ShowScanned(DiskScan.Run(_root, new ScanOptions { ListFilesFrom = 1 }, null, CancellationToken.None));
        Assert.Contains(model.Entries, e => e.Name == "games");

        var hits = model.Search("shader", 5);

        var hit = Assert.Single(hits);
        Assert.Equal("shader_cache.bin", hit.Title);
        Assert.Contains("deep", hit.Detail);

        hit.Open();

        // The listing moved down to the folder the file sits in, and the file is selected.
        Assert.EndsWith("deep", model.CurrentPath);
        Assert.Equal("shader_cache.bin", model.Selected?.Name);
    }

    [Fact]
    public void Chonk_offers_the_biggest_match_first_and_never_the_rolled_up_row()
    {
        Directory.CreateDirectory(Path.Combine(_root, "a"));
        File.WriteAllBytes(Path.Combine(_root, "a", "big file.bin"), new byte[8192]);
        File.WriteAllBytes(Path.Combine(_root, "a", "small file.bin"), new byte[100]);
        File.WriteAllBytes(Path.Combine(_root, "file.bin"), new byte[4096]);

        using var model = new ChonkViewModel(new FakeHost(Path.Combine(_root, "hostdata")));
        model.ShowScanned(DiskScan.Run(_root, new ScanOptions { ListFilesFrom = 1024 }, null, CancellationToken.None));

        var titles = model.Search("file", 10).Select(h => h.Title).ToList();

        // "small file.bin" is under the listing threshold, so it lives in the rolled-up row and
        // is not a thing one can land on.
        Assert.Equal(["big file.bin", "file.bin"], titles);
    }

    [Fact]
    public void Kibble_finds_a_file_past_the_window_and_scrolls_down_to_it()
    {
        using var temp = new TempWorkspace();
        temp.WriteConfig(temp.AddGroup("Alpha"));
        var folder = Path.Combine(temp.Root, "intake");
        Directory.CreateDirectory(folder);
        for (var i = 0; i < 30; i++)
            File.WriteAllBytes(Path.Combine(folder, $"page{i:00}.png"), [1]);
        File.WriteAllBytes(Path.Combine(folder, "zzz needle.png"), [1]);

        var model = new KibbleViewModel(new FakeHost(Path.Combine(temp.Root, "hostdata")));
        model.SetBotRoot(temp.Workspace.Root);
        model.PageSize = 10;
        model.LazyLoad = true;
        model.LoadFolder(folder);
        var window = model.Incoming.Count;
        Assert.True(window < 31, "the grid should start with a window rather than everything");
        Assert.DoesNotContain(model.Incoming, f => f.FileName == "zzz needle.png");

        var hit = Assert.Single(model.Search("needle", 5));
        hit.Open();

        Assert.Equal("zzz needle.png", model.Selected?.FileName);
        Assert.Contains(model.Incoming, f => f.FileName == "zzz needle.png");
    }

    [Fact]
    public void Kibble_does_not_offer_destinations_because_landing_on_one_would_send()
    {
        using var temp = new TempWorkspace();
        temp.WriteConfig(temp.AddGroup("Alpha"));
        var folder = Path.Combine(temp.Root, "intake");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "a.png"), [1]);

        var model = new KibbleViewModel(new FakeHost(Path.Combine(temp.Root, "hostdata")));
        model.SetBotRoot(temp.Workspace.Root);
        model.LoadFolder(folder);

        Assert.Empty(model.Search("Alpha", 5));
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
