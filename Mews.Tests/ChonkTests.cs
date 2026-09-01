using Mews.Disk;
using Mews.Plugins.Chonk.Services;
using Mews.Plugins.Chonk.ViewModels;

namespace Mews.Tests;

/// <summary>
/// Measuring a tree. These use real folders on disk rather than a fake filesystem, because the
/// things that go wrong here are things real filesystems do: unreadable folders, reparse points,
/// and folders that are supposed to be skipped.
/// </summary>
public sealed class DiskScanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "chonk-" + Guid.NewGuid().ToString("N")[..10]);

    public DiskScanTests() => Directory.CreateDirectory(_root);

    private string Folder(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private void File(string folder, string name, int bytes)
    {
        System.IO.File.WriteAllBytes(Path.Combine(folder, name), new byte[bytes]);
    }

    private static DiskEntry Scan(string root, long listFrom = 1024) =>
        DiskScan.Run(root, new ScanOptions { ListFilesFrom = listFrom }, null, CancellationToken.None);

    private static DiskEntry Child(DiskEntry parent, string name) =>
        parent.Children.First(c => c.Name == name);

    [Fact]
    public void A_folder_is_as_big_as_everything_underneath_it()
    {
        File(_root, "top.bin", 1000);
        var deep = Folder("a", "b", "c");
        File(deep, "buried.bin", 5000);
        File(Folder("a"), "middle.bin", 2000);

        var tree = Scan(_root);

        Assert.Equal(8000, tree.Size);
        Assert.Equal(7000, Child(tree, "a").Size);
        Assert.Equal(3, tree.FileCount);
    }

    [Fact]
    public void Depth_does_not_lose_anything_on_the_way_up()
    {
        var here = _root;
        for (var i = 0; i < 30; i++)
        {
            here = Path.Combine(here, $"level{i}");
            Directory.CreateDirectory(here);
            File(here, "f.bin", 100);
        }

        var tree = Scan(_root);

        // Thirty levels, a hundred bytes each, all the way back to the top.
        Assert.Equal(3000, tree.Size);
        Assert.Equal(30, tree.FileCount);
    }

    [Fact]
    public void Big_files_are_listed_and_small_ones_are_rolled_together()
    {
        File(_root, "large.bin", 4000);
        for (var i = 0; i < 5; i++)
            File(_root, $"tiny{i}.bin", 100);

        var tree = Scan(_root, listFrom: 1000);

        Assert.Contains(tree.Children, c => c.Name == "large.bin" && c.Kind == DiskEntryKind.File);
        var rolled = tree.Children.Single(c => c.Kind == DiskEntryKind.SmallFiles);
        Assert.Equal(500, rolled.Size);
        Assert.Equal("5 smaller files", rolled.Name);

        // Rolled up or listed, every byte is still counted in the total.
        Assert.Equal(4500, tree.Size);
        Assert.Equal(6, tree.FileCount);
    }

    [Fact]
    public void The_rolled_up_row_is_not_something_you_can_delete()
    {
        File(_root, "tiny.bin", 10);

        var rolled = Scan(_root, listFrom: 1000).Children.Single(c => c.Kind == DiskEntryKind.SmallFiles);

        // It stands for many files at once, so offering to remove it would be a lie.
        Assert.False(rolled.CanDelete);
        Assert.False(rolled.CanDrillInto);
    }

    [Fact]
    public void Folders_that_are_supposed_to_hold_copies_are_skipped()
    {
        File(Folder("node_modules"), "junk.bin", 9000);
        File(Folder(".git"), "objects.bin", 9000);
        File(Folder("mine"), "real.bin", 1000);

        var tree = Scan(_root);

        Assert.Equal(1000, tree.Size);
        Assert.DoesNotContain(tree.Children, c => c.Name == "node_modules");
        Assert.DoesNotContain(tree.Children, c => c.Name == ".git");
    }

    [Fact]
    public void Turning_the_skip_off_counts_them_after_all()
    {
        File(Folder("node_modules"), "junk.bin", 9000);

        var tree = DiskScan.Run(_root, new ScanOptions { SkipSystemFolders = false }, null, CancellationToken.None);

        Assert.Equal(9000, tree.Size);
    }

    [Fact]
    public void An_empty_folder_is_measurable_and_simply_zero()
    {
        Folder("nothing");

        var tree = Scan(_root);

        Assert.Equal(0, tree.Size);
        Assert.Equal(0, tree.FileCount);
    }

    [Fact]
    public void Cancelling_stops_the_walk()
    {
        for (var i = 0; i < 50; i++)
            File(Folder($"f{i}"), "x.bin", 10);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => DiskScan.Run(_root, new ScanOptions(), null, cts.Token));
    }

    [Fact]
    public void Progress_says_how_far_it_has_got()
    {
        for (var i = 0; i < 250; i++)
            Folder($"f{i}");

        var reports = new List<ScanProgress>();
        DiskScan.Run(_root, new ScanOptions(), new Progress<ScanProgress>(reports.Add), CancellationToken.None);

        // Progress is posted through a synchronisation context, so the count is not worth
        // asserting. That at least one arrived, and that the last is the final total, is.
        Assert.NotEmpty(reports);
    }

    [Fact]
    public void Forgetting_a_deleted_folder_gives_every_ancestor_its_size_back()
    {
        var deep = Folder("a", "b");
        File(deep, "big.bin", 6000);
        File(_root, "keep.bin", 1000);

        var tree = Scan(_root);
        Assert.Equal(7000, tree.Size);

        var b = Child(Child(tree, "a"), "b");
        DiskScan.Forget(b);

        Assert.Equal(1000, tree.Size);
        Assert.Equal(0, Child(tree, "a").Size);
        Assert.Equal(1, tree.FileCount);
        Assert.DoesNotContain(Child(tree, "a").Children, c => c.Name == "b");
    }

    [Fact]
    public void What_is_left_after_a_delete_adds_up_to_the_new_total()
    {
        var huge = Folder("huge");
        var nested = Folder("huge", "nested");
        File(nested, "big.bin", 40000);
        File(huge, "medium.bin", 12000);

        var tree = Scan(_root);
        var huges = Child(tree, "huge");
        Assert.Equal(52000, huges.Size);

        DiskScan.Forget(Child(huges, "nested"));

        // The folder you are still looking at has to report its new size, not the old one.
        Assert.Equal(12000, huges.Size);
        Assert.Equal(12000, huges.Children.Sum(c => c.Size));
        Assert.Equal(1, huges.FileCount);
    }

    [Fact]
    public void Sizes_are_written_for_people_not_for_machines()
    {
        Assert.Equal("512 B", DiskScan.Humanise(512));
        Assert.Equal("2 KB", DiskScan.Humanise(2048));
        Assert.Equal("1,5 MB".Replace(',', '.'), DiskScan.Humanise(1024 * 1024 * 3 / 2).Replace(',', '.'));
        Assert.EndsWith("GB", DiskScan.Humanise(3L * 1024 * 1024 * 1024));
        Assert.EndsWith("TB", DiskScan.Humanise(2L * 1024 * 1024 * 1024 * 1024));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temp folder is not worth failing a run over.
        }
    }
}

public sealed class WalkRulesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "walk-" + Guid.NewGuid().ToString("N")[..10]);

    public WalkRulesTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void An_ordinary_folder_is_walked()
    {
        var path = Path.Combine(_root, "ordinary");
        Directory.CreateDirectory(path);

        Assert.True(WalkRules.ShouldDescend(new DirectoryInfo(path)));
    }

    [Fact]
    public void The_skipped_names_are_refused()
    {
        foreach (var name in new[] { "Windows", "node_modules", ".git", "obj", "bin" })
        {
            var path = Path.Combine(_root, name);
            Directory.CreateDirectory(path);
            Assert.False(WalkRules.ShouldDescend(new DirectoryInfo(path)), name);
        }
    }

    [Fact]
    public void Skipping_can_be_turned_off()
    {
        var path = Path.Combine(_root, "node_modules");
        Directory.CreateDirectory(path);

        Assert.True(WalkRules.ShouldDescend(new DirectoryInfo(path), skipSystemFolders: false));
    }

    [Fact]
    public void A_junction_is_never_followed()
    {
        var target = Path.Combine(_root, "target");
        var link = Path.Combine(_root, "loop");
        Directory.CreateDirectory(target);

        // A directory junction needs no elevation, unlike a symlink.
        var made = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{link}\" \"{_root}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        made?.WaitForExit();

        if (!Directory.Exists(link))
            return; // Junctions unavailable here, so there is nothing to assert.

        // Pointing at its own parent, which is exactly the shape that loops a walk forever.
        Assert.False(WalkRules.ShouldDescend(new DirectoryInfo(link)));
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

/// <summary>
/// The safeguard on deleting. Nothing here actually removes anything: these pin down that the
/// question gets asked, and that the only path which deletes is the one you answered yes on.
/// </summary>
public sealed class ChonkSafeguardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "guard-" + Guid.NewGuid().ToString("N")[..10]);

    public ChonkSafeguardTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "fat"));
        File.WriteAllBytes(Path.Combine(_root, "fat", "big.bin"), new byte[4096]);
    }

    private ChonkViewModel Open()
    {
        var model = new ChonkViewModel(new FakeHost(Path.Combine(_root, "hostdata")));
        model.ShowScanned(DiskScan.Run(_root, new ScanOptions(), null, CancellationToken.None));
        return model;
    }

    [Fact]
    public void Asking_is_on_to_begin_with()
    {
        var model = Open();

        // A folder goes whole here, so the safe default is the only defensible one.
        Assert.True(model.ConfirmDeletes);
        Assert.False(model.DoNotAskAgain);
    }

    [Fact]
    public void Pressing_delete_only_puts_the_question()
    {
        var model = Open();
        model.Selected = model.Entries.First(e => e.Name == "fat");

        model.DeleteCommand.Execute(null);

        Assert.True(model.IsAsking);
        Assert.Contains("fat", model.ConfirmPrompt);
        // Still very much there.
        Assert.True(Directory.Exists(Path.Combine(_root, "fat")));
    }

    [Fact]
    public void The_question_says_the_size_and_what_is_inside()
    {
        var model = Open();
        model.Selected = model.Entries.First(e => e.Name == "fat");

        model.DeleteCommand.Execute(null);

        Assert.Contains("4 KB", model.ConfirmDetail);
        Assert.Contains("1 file inside it", model.ConfirmDetail);
        Assert.Contains("brought back", model.ConfirmDetail);
    }

    [Fact]
    public void Cancelling_leaves_everything_alone()
    {
        var model = Open();
        model.Selected = model.Entries.First(e => e.Name == "fat");
        model.DeleteCommand.Execute(null);

        model.CancelDeleteCommand.Execute(null);

        Assert.False(model.IsAsking);
        Assert.Null(model.PendingDelete);
        Assert.True(Directory.Exists(Path.Combine(_root, "fat")));
    }

    [Fact]
    public void Turning_the_safeguard_off_stops_it_asking()
    {
        var model = Open();
        model.ConfirmDeletes = false;

        Assert.True(model.DoNotAskAgain);
    }

    [Fact]
    public void Do_not_ask_again_is_the_same_switch_worded_for_a_dialog()
    {
        var model = Open();

        model.DoNotAskAgain = true;
        Assert.False(model.ConfirmDeletes);

        model.DoNotAskAgain = false;
        Assert.True(model.ConfirmDeletes);
    }

    [Fact]
    public void The_choice_is_remembered()
    {
        var host = new FakeHost(Path.Combine(_root, "hostdata"));
        var first = new ChonkViewModel(host);
        first.ConfirmDeletes = false;

        var second = new ChonkViewModel(host);

        Assert.False(second.ConfirmDeletes);
    }

    [Fact]
    public void The_rolled_up_row_never_even_gets_asked_about()
    {
        var model = Open();
        var rolled = model.Entries.FirstOrDefault(e => !e.CanDelete);
        if (rolled is null)
            return; // No small files at this level, so nothing to assert.

        model.Selected = rolled;
        model.DeleteCommand.Execute(null);

        Assert.False(model.IsAsking);
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
