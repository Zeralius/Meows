using Mews.Plugins.Molt.Services;
using Mews.Plugins.Molt.ViewModels;

namespace Mews.Tests;

public sealed class MoltCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "molt-" + Guid.NewGuid().ToString("N")[..10]);

    public MoltCatalogTests() => Directory.CreateDirectory(_root);

    private string Folder(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void File(string folder, string name, int bytes) =>
        System.IO.File.WriteAllBytes(Path.Combine(folder, name), new byte[bytes]);

    /// <summary>
    /// Everything pointed at the throwaway tree, so these never measure the real machine. That
    /// keeps them quick and, more importantly, keeps them saying the same thing on any machine.
    /// </summary>
    private MoltOptions Options(string? buildRoot) => new()
    {
        BuildRoot = buildRoot,
        LocalAppData = Path.Combine(_root, "local"),
        UserProfile = Path.Combine(_root, "profile"),
        TempFolder = Path.Combine(_root, "temp"),
    };

    private IReadOnlyList<Sheddable> Build() =>
        MoltCatalog.Build(Options(_root), null, CancellationToken.None);

    private Sheddable BuildOutput() => Build().Single(s => s.Id == "build");

    [Fact]
    public void Bin_and_obj_under_the_chosen_folder_are_found()
    {
        File(Folder("ProjectA", "bin"), "a.dll", 5000);
        File(Folder("ProjectA", "obj"), "a.pdb", 3000);
        File(Folder("ProjectB", "bin"), "b.dll", 2000);

        var build = BuildOutput();

        Assert.Equal(3, build.Paths.Count);
        Assert.Equal(10000, build.Size);
    }

    [Fact]
    public void Source_next_to_the_output_is_never_touched()
    {
        File(Folder("ProjectA"), "Program.cs", 900);
        File(Folder("ProjectA", "bin"), "a.dll", 5000);

        var build = BuildOutput();

        Assert.Single(build.Paths);
        Assert.EndsWith("bin", build.Paths[0]);
        Assert.Equal(5000, build.Size);
    }

    [Fact]
    public void A_bin_is_taken_whole_rather_than_walked_into()
    {
        var bin = Folder("ProjectA", "bin");
        File(bin, "a.dll", 1000);
        File(Folder("ProjectA", "bin", "Debug", "net10.0"), "deep.dll", 4000);

        var build = BuildOutput();

        // One path, and its size includes everything underneath it.
        Assert.Single(build.Paths);
        Assert.Equal(5000, build.Size);
    }

    [Fact]
    public void Folders_that_would_only_slow_the_hunt_are_skipped()
    {
        File(Folder("ProjectA", "node_modules", "pkg", "bin"), "nested.dll", 9000);
        File(Folder("ProjectA", ".git", "bin"), "gitbin.dll", 9000);
        File(Folder("ProjectA", "bin"), "real.dll", 1000);

        var build = BuildOutput();

        Assert.Single(build.Paths);
        Assert.Equal(1000, build.Size);
    }

    [Fact]
    public void With_no_folder_chosen_nothing_is_hunted_for()
    {
        var build = MoltCatalog.Build(Options(null), null, CancellationToken.None)
            .FirstOrDefault(s => s.Id == "build");

        // Absent entirely rather than present and empty: there is nothing to say yet.
        Assert.Null(build);
    }

    [Fact]
    public void Every_entry_says_what_it_costs_to_lose()
    {
        File(Folder("ProjectA", "bin"), "a.dll", 100);

        foreach (var entry in Build())
        {
            // "Safe to delete" is a claim, and each one owes its reasoning.
            Assert.False(string.IsNullOrWhiteSpace(entry.What), entry.Id);
            Assert.False(string.IsNullOrWhiteSpace(entry.Cost), entry.Id);
            Assert.False(string.IsNullOrWhiteSpace(entry.Where), entry.Id);
        }
    }

    [Fact]
    public void Nothing_empty_is_offered()
    {
        // No bin, no obj, so the build entry has nothing to say and is left out.
        Folder("ProjectA");

        Assert.DoesNotContain(Build(), s => s.Id == "build");
        Assert.All(Build(), s => Assert.NotEmpty(s.Paths));
    }

    [Fact]
    public void Cancelling_stops_the_hunt()
    {
        File(Folder("ProjectA", "bin"), "a.dll", 100);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => MoltCatalog.Build(Options(_root), null, cts.Token));
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

public sealed class ShedderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shed-" + Guid.NewGuid().ToString("N")[..10]);

    public ShedderTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Permanent_removes_files_and_folders_outright()
    {
        var file = Path.Combine(_root, "a.bin");
        File.WriteAllBytes(file, new byte[100]);
        var folder = Path.Combine(_root, "deep");
        Directory.CreateDirectory(Path.Combine(folder, "inner"));
        File.WriteAllBytes(Path.Combine(folder, "inner", "b.bin"), new byte[100]);

        var result = Shedder.Shed([file, folder], ShedMode.Permanent, 200);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Removed);
        Assert.False(File.Exists(file));
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void One_locked_file_does_not_stop_the_others()
    {
        var locked = Path.Combine(_root, "locked.bin");
        var free = Path.Combine(_root, "free.bin");
        File.WriteAllBytes(locked, new byte[100]);
        File.WriteAllBytes(free, new byte[100]);

        using (var hold = File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = Shedder.Shed([locked, free], ShedMode.Permanent, 200);

            // A cache always has a few files something still has open. The rest must still go.
            Assert.False(File.Exists(free));
            Assert.True(File.Exists(locked));
            Assert.Equal(1, result.Removed);
            Assert.Equal(1, result.Failed);
            Assert.Contains("in use", result.FailureReason!);
        }
    }

    [Fact]
    public void Nothing_to_shed_is_a_quiet_success()
    {
        var result = Shedder.Shed([], ShedMode.Permanent, 0);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.Removed);
    }

    [Fact]
    public void A_path_that_is_already_gone_is_not_a_failure()
    {
        var result = Shedder.Shed([Path.Combine(_root, "never-existed")], ShedMode.Permanent, 0);

        Assert.Equal(0, result.Removed);
        Assert.Equal(0, result.Failed);
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
/// Picking. The rows carry their own tick, so the thing worth pinning down is that the view
/// model hears about it: without that the button stays dead however many boxes you tick.
/// </summary>
public sealed class MoltPickingTests
{
    private static MoltViewModel Model(out List<Sheddable> found)
    {
        var host = new FakeHost(Path.Combine(Path.GetTempPath(), "moltvm-" + Guid.NewGuid().ToString("N")[..8]));
        var model = new MoltViewModel(host);

        found =
        [
            Fake("a", "First", 3000),
            Fake("b", "Second", 2000),
        ];
        model.Show(found);
        return model;
    }

    private static Sheddable Fake(string id, string name, long size)
    {
        var item = new Sheddable
        {
            Id = id, Name = name, Where = @"C:\somewhere", What = "what it is", Cost = "what it costs",
            Size = size,
        };
        item.Paths.Add(@"C:\somewhere	hing");
        return item;
    }

    [Fact]
    public void Ticking_one_row_reaches_the_count_and_the_button()
    {
        var model = Model(out _);
        Assert.Equal(0, model.PickedCount);
        Assert.False(model.ShedCommand.CanExecute(null));

        model.Items[0].IsPicked = true;

        Assert.Equal(1, model.PickedCount);
        Assert.Equal(3000, model.PickedSize);
        Assert.True(model.ShedCommand.CanExecute(null));
        Assert.Contains("1 picked", model.PickedText);
    }

    [Fact]
    public void Unticking_it_again_puts_everything_back()
    {
        var model = Model(out _);
        model.Items[0].IsPicked = true;
        model.Items[0].IsPicked = false;

        Assert.Equal(0, model.PickedCount);
        Assert.False(model.ShedCommand.CanExecute(null));
    }

    [Fact]
    public void Pick_all_and_pick_none_do_what_they_say()
    {
        var model = Model(out _);

        model.PickAllCommand.Execute(null);
        Assert.Equal(2, model.PickedCount);
        Assert.Equal(5000, model.PickedSize);

        model.PickNoneCommand.Execute(null);
        Assert.Equal(0, model.PickedCount);
    }

    [Fact]
    public void Biggest_first_so_the_worst_offender_is_at_the_top()
    {
        var model = Model(out _);

        Assert.Equal("First", model.Items[0].Name);
        Assert.Equal("Second", model.Items[1].Name);
    }

    [Fact]
    public void The_mode_line_says_which_guarantee_you_are_getting()
    {
        var model = Model(out _);

        Assert.Contains("Recycle Bin", model.ModeText);
        model.Permanent = true;
        Assert.Contains("nothing can be undone", model.ModeText);
    }

    [Fact]
    public void Rescanning_does_not_leave_old_rows_wired_up()
    {
        var model = Model(out _);
        var stale = model.Items[0];
        model.Show([Fake("c", "Fresh", 1000)]);

        // The old row is gone from the list, and poking it must not move the new count.
        stale.IsPicked = true;

        Assert.Single(model.Items);
        Assert.Equal(0, model.PickedCount);
    }
}
