using Meows.Disk;
using Meows.Plugins.Chonk.Services;
using Meows.Plugins.Litter.Services;
using Meows.Plugins.Mouser.Services;

namespace Meows.Tests;

/// <summary>
/// Every scanner goes through <see cref="FolderWalk"/> now, so a fix applies everywhere. These
/// check that, using the folder shape that sent one scanner into an infinite loop while the
/// others happened to walk past it.
/// </summary>
public sealed class FolderWalkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "walk-" + Guid.NewGuid().ToString("N")[..10]);

    public FolderWalkTests() => Directory.CreateDirectory(_root);

    /// <summary>A folder holding a real file and, beside it, one named with a single space.</summary>
    private string Awkward()
    {
        var game = Path.Combine(_root, "TOK 2");
        Directory.CreateDirectory(game);
        File.WriteAllBytes(Path.Combine(game, "real.dat"), new byte[64]);
        Directory.CreateDirectory(@"\\?\" + Path.Combine(game, " "));
        return game;
    }

    [Fact]
    public void A_child_that_is_really_its_own_parent_is_not_offered_as_somewhere_to_go()
    {
        var game = new DirectoryInfo(Awkward());

        var children = FolderWalk.Into(game);

        // The space folder is a genuine child and worth visiting; what must not happen is the
        // walk being handed the parent back under a different name.
        Assert.DoesNotContain(children, c => c.FullName.TrimEnd(Path.DirectorySeparatorChar)
            .Equals(game.FullName.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_unreadable_folder_contributes_nothing_rather_than_throwing()
    {
        var missing = new DirectoryInfo(Path.Combine(_root, "not here at all"));

        Assert.Empty(FolderWalk.Into(missing));
        Assert.Empty(FolderWalk.Files(missing));
        Assert.False(FolderWalk.CanRead(missing));
    }

    [Fact]
    public void Chonk_finishes_on_the_folder_that_used_to_loop()
    {
        var game = Awkward();
        using var source = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var top = DiskScan.Run(game, new ScanOptions(), null, source.Token);

        Assert.False(source.IsCancellationRequested, "Chonk is looping again");
        Assert.Equal(64, top.Size);
    }

    [Fact]
    public void Mouser_finishes_on_the_folder_that_used_to_loop()
    {
        var game = Awkward();
        using var source = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var result = MouserScan.Run(game, new MouserOptions(), null, source.Token);

        Assert.False(source.IsCancellationRequested, "Mouser is looping");
        Assert.False(result.WasStopped);
    }

    [Fact]
    public async Task Measuring_a_folder_finishes_on_the_one_that_used_to_loop()
    {
        var game = new DirectoryInfo(Awkward());

        // FolderSize is called from inside Molt and Litter, so a loop here would strand both.
        // A timeout rather than an assertion, because a loop never returns to be asserted about.
        var size = await Task.Run(() => FolderSize.Of(game)).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(64, size);
    }

    [Fact]
    public async Task Litter_finishes_on_the_folder_that_used_to_loop()
    {
        Awkward();

        var items = await Task.Run(() => LitterScan.Read(_root, DateTime.Now))
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Contains(items, i => i.Name == "TOK 2");
    }

    [Fact]
    public async Task Working_out_what_a_folder_is_finishes_on_the_one_that_used_to_loop()
    {
        var game = Awkward();

        var identity = await Task.Run(() => FolderInspector.Of(game))
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.NotNull(identity);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(@"\\?\" + _root, recursive: true);
        }
        catch (Exception)
        {
        }
    }
}
