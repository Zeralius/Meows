using Meows.Disk;
using Meows.Plugins.Chonk.Services;

namespace Meows.Tests;

/// <summary>
/// Windows strips a trailing space or dot from a path. Both bugs covered here came from one
/// real folder named " " inside a Steam game called TOK 2.
/// </summary>
public sealed class AwkwardNameTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "awkward-" + Guid.NewGuid().ToString("N")[..10]);

    public AwkwardNameTests() => Directory.CreateDirectory(_root);

    /// <summary>
    /// Names ending in a space or a dot cannot be created through the normal path APIs, which
    /// is part of why they are so rarely encountered.
    /// </summary>
    private string Awkward(string parent, string name)
    {
        var full = Path.Combine(_root, parent);
        Directory.CreateDirectory(full);
        Directory.CreateDirectory(@"\\?\" + Path.Combine(full, name));
        return Directory.GetDirectories(full).Single();
    }

    [Fact]
    public void A_folder_named_with_a_trailing_space_does_not_survive_normalising()
    {
        var space = Awkward("game", " ");

        Assert.False(WalkRules.SurvivesNormalising(space));
        // It normalises to the parent, which is the whole problem.
        Assert.NotEqual(space, Path.GetFullPath(space));
    }

    [Fact]
    public void An_ordinary_path_is_not_refused_for_no_reason()
    {
        var ordinary = Path.Combine(_root, "perfectly normal");
        Directory.CreateDirectory(ordinary);

        Assert.True(WalkRules.SurvivesNormalising(ordinary));
        Assert.True(WalkRules.SurvivesNormalising(ordinary + Path.DirectorySeparatorChar));
        // Relative paths are fine too. Only the last segment is compared, so being relative is
        // not itself grounds for refusal.
        Assert.True(WalkRules.SurvivesNormalising("some/relative/path"));
    }

    [Fact]
    public void Deleting_a_name_ending_in_a_dot_does_not_take_the_folder_beside_it()
    {
        // The real one. "data." normalises to "data", so the delete lands next door. Before this
        // was fixed the call removed data and everything in it, and reported success.
        var keep = Path.Combine(_root, "pair", "data");
        Directory.CreateDirectory(keep);
        File.WriteAllText(Path.Combine(keep, "precious.txt"), "must survive");

        Directory.CreateDirectory(@"\\?\" + Path.Combine(_root, "pair", "data."));
        var odd = Directory.GetDirectories(Path.Combine(_root, "pair")).Single(d => d.EndsWith("."));

        var outcome = RecycleBin.Send([odd]);

        Assert.False(outcome.Succeeded);
        Assert.Contains("space or a dot", outcome.FailureReason);
        Assert.True(File.Exists(Path.Combine(keep, "precious.txt")), "the folder next door was destroyed");
        Assert.True(Directory.Exists(keep));
    }

    [Fact]
    public void Refusing_one_awkward_name_does_not_stop_the_rest_being_removed()
    {
        var ordinary = Path.Combine(_root, "mixed", "ordinary");
        Directory.CreateDirectory(ordinary);
        File.WriteAllText(Path.Combine(ordinary, "junk.txt"), "throwaway");

        Directory.CreateDirectory(@"\\?\" + Path.Combine(_root, "mixed", "odd."));
        var odd = Directory.GetDirectories(Path.Combine(_root, "mixed")).Single(d => d.EndsWith("."));

        var outcome = RecycleBin.Send([ordinary, odd]);

        Assert.Equal(1, outcome.Deleted);
        Assert.Equal(1, outcome.Failed);
        Assert.False(Directory.Exists(ordinary));
        Assert.Contains("space or a dot", outcome.FailureReason);
    }

    [Fact]
    public void A_walk_does_not_go_round_forever_on_a_folder_named_with_a_space()
    {
        // Rebuilding a DirectoryInfo from the path string turns this folder back into its own
        // parent, and the scan then walks the pair until it is stopped. It took a real scan to
        // 45 TB across five million folders on a machine holding eighteen.
        Awkward("game", " ");

        using var source = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var top = DiskScan.Run(Path.Combine(_root, "game"), new ScanOptions(), null, source.Token);

        Assert.False(source.IsCancellationRequested, "the scan did not finish, so it is looping again");
        Assert.NotNull(top);
    }

    [Fact]
    public void A_child_that_resolves_back_to_its_parent_is_not_walked_into()
    {
        var parent = new DirectoryInfo(_root);
        var itself = new DirectoryInfo(_root + Path.DirectorySeparatorChar);

        Assert.False(WalkRules.LeadsSomewhereNew(itself, parent));

        var real = Directory.CreateDirectory(Path.Combine(_root, "genuine child"));
        Assert.True(WalkRules.LeadsSomewhereNew(real, parent));
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
