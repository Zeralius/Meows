using Meows.Plugins.WeighIn.Services;
using Meows.Plugins.WeighIn.ViewModels;

namespace Meows.Tests;

/// <summary>
/// Weigh-In: a reading kept to a depth, the arithmetic between two of them, and the tab saying
/// which folders explain a drive's growth over a window.
/// </summary>
public sealed class WeighInTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "weighin-" + Guid.NewGuid().ToString("N")[..10]);

    public WeighInTests() => Directory.CreateDirectory(_root);

    private string Folder(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void File(string folder, string name, int bytes) =>
        System.IO.File.WriteAllBytes(Path.Combine(folder, name), new byte[bytes]);

    private const long GB = 1L << 30;

    private static DriveReading Drive(string root, long used, params (string Path, long Size)[] folders) =>
        new(root, 1000 * GB, 1000 * GB - used, folders.Select(f => new FolderReading(f.Path, f.Size)).ToList());

    [Fact]
    public void A_reading_keeps_folder_totals_to_the_depth_and_no_files()
    {
        var drive = Folder("drive");
        File(Folder("drive", "games"), "a.bin", 5000);
        File(Folder("drive", "games", "steam"), "b.bin", 3000);
        File(Folder("drive", "games", "steam", "deep"), "c.bin", 1000);
        File(Folder("drive", "docs"), "d.bin", 200);

        var reading = Readings.Take([drive], depth: 2, skipSystemFolders: true, null, CancellationToken.None);

        var only = Assert.Single(reading.Drives);
        Assert.Equal(drive, only.Root);
        var paths = only.Folders.Select(f => Path.GetRelativePath(drive, f.Path)).ToList();
        Assert.Contains("games", paths);
        Assert.Contains(Path.Combine("games", "steam"), paths);
        Assert.Contains("docs", paths);
        // Depth two: the third level is inside its parent's total, not a row of its own.
        Assert.DoesNotContain(Path.Combine("games", "steam", "deep"), paths);
        Assert.Equal(9000, only.Folders.Single(f => f.Path == Path.Combine(drive, "games")).Size);
        Assert.Equal(4000, only.Folders.Single(f => f.Path == Path.Combine(drive, "games", "steam")).Size);
    }

    [Fact]
    public void Readings_are_saved_one_per_day_loaded_in_order_and_pruned_to_a_count()
    {
        var folder = Path.Combine(_root, "readings");
        for (var i = 0; i < 5; i++)
            Readings.Save(folder, new Reading(new DateTime(2026, 9, 1 + i), [Drive(@"F:\", 100 + i)]));

        var loaded = Readings.Load(folder);
        Assert.Equal(5, loaded.Count);
        Assert.True(loaded.Zip(loaded.Skip(1)).All(p => p.First.At < p.Second.At));

        Assert.Equal(3, Readings.Prune(folder, 2));
        Assert.Equal(2, Readings.Load(folder).Count);
        Assert.Equal(new DateTime(2026, 9, 4), Readings.Load(folder)[0].At);

        Assert.Equal(new DateTime(2026, 9, 4), Readings.Before(Readings.Load(folder), new DateTime(2026, 9, 4, 12, 0, 0))!.At);
        Assert.Null(Readings.Before(Readings.Load(folder), new DateTime(2026, 8, 1)));
    }

    [Fact]
    public void Comparing_two_readings_lists_what_moved_by_more_than_the_threshold_biggest_first()
    {
        var before = Drive(@"F:\", 500, (@"F:\games", 400), (@"F:\docs", 50), (@"F:\old", 30));
        var after = Drive(@"F:\", 700, (@"F:\games", 600), (@"F:\docs", 51), (@"F:\new", 20));

        var moved = Readings.Compare(before, after, threshold: 10);

        Assert.Equal([@"F:\games", @"F:\old", @"F:\new"], moved.Select(g => g.Path));
        Assert.Equal(200, moved[0].Delta);
        Assert.True(moved[1].IsGone);
        Assert.True(moved[2].IsNew);

        // No earlier reading: everything is new.
        Assert.All(Readings.Compare(null, after, 10), g => Assert.True(g.IsNew));
    }

    [Fact]
    public void The_folders_responsible_are_the_deepest_ones_without_their_parents()
    {
        var moved = new List<Growth>
        {
            new(@"F:\Steam", 0, 200),
            new(@"F:\Steam\steamapps", 0, 200),
            new(@"F:\Steam\steamapps\common", 0, 190),
            new(@"F:\Photos", 0, 40),
            new(@"F:\Photos\2026", 0, 15),
        };

        var responsible = Readings.Responsible(moved, 10).Select(g => g.Path).ToList();

        // Steam's growth is its child's growth, twice over; the deepest is the answer.
        Assert.Equal([@"F:\Steam\steamapps\common", @"F:\Photos", @"F:\Photos\2026"], responsible);
    }

    [Fact]
    public void The_tab_says_what_a_drive_lost_over_the_window_and_which_folders_did_it()
    {
        var host = new FakeHost(Path.Combine(_root, "hostdata"));
        var folder = Path.Combine(host.DataDirectory, "readings");
        Readings.Save(folder, new Reading(DateTime.Now.AddDays(-10), [Drive(@"F:\", 500 * GB, (@"F:\games", 400 * GB), (@"F:\docs", 50 * GB))]));
        Readings.Save(folder, new Reading(DateTime.Now.AddDays(-2), [Drive(@"F:\", 700 * GB, (@"F:\games", 600 * GB), (@"F:\docs", 50 * GB))]));

        using var model = new WeighInViewModel(host);

        // Readings exist, so nothing is walked on opening, but the daily schedule is registered.
        Assert.DoesNotContain(host.Work.Requested, t => t == "Reading the drives");
        Assert.Contains(host.Work.Scheduled, s => s.Title == "Daily drive reading");

        var drive = Assert.Single(model.Drives);
        Assert.Equal(200 * GB, drive.Delta);
        Assert.True(drive.Grew);
        Assert.Contains("lost 200 GB", model.DriveHeadline);
        var growth = Assert.Single(model.Growth);
        Assert.Equal(@"F:\games", growth.Path);
        Assert.Equal("+200 GB", growth.DeltaText);

        // A window with no reading that old falls back to the oldest one there is.
        model.Window = WeighInViewModel.Windows.Single(w => w.Days == 90);
        Assert.Equal(200 * GB, model.Drives.Single().Delta);

        var hit = Assert.Single(model.Search("games", 5));
        hit.Open();
        Assert.Equal(@"F:\games", model.SelectedGrowth?.Path);
    }

    [Fact]
    public void With_no_readings_the_first_one_is_taken_at_once()
    {
        var host = new FakeHost(Path.Combine(_root, "hostdata-fresh"));

        using var model = new WeighInViewModel(host);

        Assert.Contains(host.Work.Requested, t => t == "Reading the drives");
        Assert.True(model.IsEmpty);
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
