using Meows.Disk;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Carry.ViewModels;

namespace Meows.Tests;

/// <summary>
/// Carry: refuses before writing anything when it should, copies and checks every file, leaves a
/// link that leads to the copy, removes the original only after that, puts everything back when
/// a step fails, and can bring a folder home again. Off Windows a symbolic link stands in for the
/// junction, which is all the tests need of it.
/// </summary>
public sealed class CarryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "carry-" + Guid.NewGuid().ToString("N")[..10]);
    private readonly string _c;
    private readonly string _d;

    public CarryTests()
    {
        _c = Path.Combine(_root, "C");
        _d = Path.Combine(_root, "D");
        Directory.CreateDirectory(_c);
        Directory.CreateDirectory(_d);
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

    /// <summary>Two pretend drives under the temp folder, told apart by which one a path is under.</summary>
    private CarryDrive? Drives(string path, long free = 10_000_000, DriveType type = DriveType.Fixed)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(_c, StringComparison.Ordinal))
            return new CarryDrive(_c, DriveType.Fixed, free);
        if (full.StartsWith(_d, StringComparison.Ordinal))
            return new CarryDrive(_d, type, free);
        return null;
    }

    private string Videos()
    {
        var videos = Path.Combine(_c, "Users", "me", "Videos");
        Directory.CreateDirectory(Path.Combine(videos, "clips", "empty"));
        File.WriteAllBytes(Path.Combine(videos, "a.mp4"), Enumerable.Range(0, 300_000).Select(i => (byte)i).ToArray());
        File.WriteAllText(Path.Combine(videos, "clips", "b.txt"), "hello");
        return videos;
    }

    [Fact]
    public void The_plan_refuses_before_anything_is_written()
    {
        var videos = Videos();

        Assert.Equal(CarryRefusal.SameDrive, FolderMove.Plan(videos, Path.Combine(_c, "other"), p => Drives(p)).Refusal);
        Assert.Equal(CarryRefusal.NotFixed, FolderMove.Plan(videos, _d, p => Drives(p, type: DriveType.Removable)).Refusal);
        Assert.Equal(CarryRefusal.NoRoom, FolderMove.Plan(videos, _d, p => Drives(p, free: 1000)).Refusal);
        Assert.Equal(CarryRefusal.NotThere, FolderMove.Plan(Path.Combine(_c, "nothing"), _d, p => Drives(p)).Refusal);
        Assert.Equal(CarryRefusal.Overlaps, FolderMove.Plan(videos, Path.Combine(videos, "clips"), p => Drives(p)).Refusal);
        Assert.True(FolderMove.IsProtected(Path.GetPathRoot(_root)!));

        var ok = FolderMove.Plan(videos, _d, p => Drives(p));
        Assert.Equal(CarryRefusal.None, ok.Refusal);
        Assert.Equal(2, ok.Files);
        Assert.Equal(300_005, ok.Bytes);
        Assert.StartsWith(Path.Combine(_d, "Carried"), ok.Target);
        Assert.EndsWith(Path.Combine("Users", "me", "Videos"), ok.Target);

        Directory.CreateDirectory(ok.Target);
        Assert.Equal(CarryRefusal.TargetThere, FolderMove.Plan(videos, _d, p => Drives(p)).Refusal);
        Assert.Empty(Directory.GetFileSystemEntries(ok.Target));
    }

    [Fact]
    public void A_carry_copies_checks_links_and_only_then_removes_the_original()
    {
        var videos = Videos();
        var stamp = new DateTime(2020, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(videos, "a.mp4"), stamp);
        var plan = FolderMove.Plan(videos, _d, p => Drives(p));

        var outcome = FolderMove.Carry(plan, null, CancellationToken.None);

        Assert.True(outcome.Done, outcome.Failure);
        Assert.Null(outcome.Left);
        Assert.Equal(plan.Target, FolderMove.LinkTarget(videos));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(videos, "clips", "b.txt")));
        Assert.True(Directory.Exists(Path.Combine(plan.Target, "clips", "empty")));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Path.Combine(plan.Target, "a.mp4")));
        Assert.False(Directory.Exists(videos + FolderMove.AsideSuffix));
        Assert.False(Directory.Exists(plan.Target + FolderMove.PartSuffix));

        // Carried once is not carried twice.
        Assert.Equal(CarryRefusal.IsLink, FolderMove.Plan(videos, _d, p => Drives(p)).Refusal);
    }

    [Fact]
    public void When_the_link_cannot_be_made_everything_goes_back()
    {
        var videos = Videos();
        var plan = FolderMove.Plan(videos, _d, p => Drives(p));

        var outcome = FolderMove.Carry(plan, null, CancellationToken.None, makeLink: (_, _) => false);

        Assert.False(outcome.Done);
        Assert.Equal("link", outcome.Failure);
        Assert.Null(FolderMove.LinkTarget(videos));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(videos, "clips", "b.txt")));
        Assert.False(Directory.Exists(plan.Target));
        Assert.False(Directory.Exists(videos + FolderMove.AsideSuffix));
    }

    [Fact]
    public void A_folder_that_changed_since_it_was_measured_is_not_carried()
    {
        var videos = Videos();
        var plan = FolderMove.Plan(videos, _d, p => Drives(p));
        File.WriteAllText(Path.Combine(videos, "new.txt"), "arrived late");

        var outcome = FolderMove.Carry(plan, null, CancellationToken.None);

        Assert.False(outcome.Done);
        Assert.Null(FolderMove.LinkTarget(videos));
        Assert.False(Directory.Exists(plan.Target));
        Assert.True(File.Exists(Path.Combine(videos, "new.txt")));
    }

    [Fact]
    public void A_carried_folder_comes_home_and_the_carried_copy_goes()
    {
        var videos = Videos();
        var plan = FolderMove.Plan(videos, _d, p => Drives(p));
        Assert.True(FolderMove.Carry(plan, null, CancellationToken.None).Done);

        var back = FolderMove.BringBack(videos, null, CancellationToken.None, p => Drives(p));

        Assert.True(back.Done, back.Failure);
        Assert.Null(FolderMove.LinkTarget(videos));
        Assert.True(Directory.Exists(videos));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(videos, "clips", "b.txt")));
        Assert.False(Directory.Exists(plan.Target));
    }

    [Fact]
    public void The_tab_records_what_it_carried_and_says_when_a_junction_leads_nowhere()
    {
        var videos = Videos();
        var host = new FakeHost(Path.Combine(_root, "host"));
        using var model = new CarryViewModel(host, () => [Drives(_c)!, Drives(_d)!], p => Drives(p), Junction.Create);

        model.ChooseDriveCommand.Execute(model.Drives.Single(d => d.Root == _d));
        model.Source = videos;
        model.PlanNow();
        Assert.True(model.CanCarry);
        Assert.Contains("2 files", model.PlanText);

        var plan = model.Plan!;
        model.Finished(plan, FolderMove.Carry(plan, null, CancellationToken.None));

        var carried = Assert.Single(model.Carried);
        Assert.Equal(CarryStanding.Fine, carried.Standing);
        Assert.Equal("", model.Source);
        Assert.Contains(host.Store.Events, e => e.Kind == "carried" && e.Subject == videos);
        Assert.False(model.Glance()!.IsTrouble);

        // The drive it went to is gone: the junction leads nowhere, and Home says so.
        Directory.Move(plan.Target, plan.Target + "-gone");
        model.RefreshCommand.Execute(null);
        Assert.Equal(CarryStanding.TargetMissing, model.Carried[0].Standing);
        Assert.True(model.Glance()!.IsTrouble);
        Assert.False(model.BringBackCommand.CanExecute(model.Carried[0]));
    }

    [Fact]
    public void A_folder_handed_over_is_measured_and_nothing_moves()
    {
        var videos = Videos();
        using var model = new CarryViewModel(new FakeHost(Path.Combine(_root, "host")), () => [], p => Drives(p), Junction.Create);
        string? answer = null;

        Assert.True(model.Accepts(Handoff.Folder(videos)));
        model.Receive(Handoff.Folder(videos) with { Reply = a => answer = a });

        Assert.Equal(videos, model.Source);
        Assert.Contains("Videos", answer);
        Assert.Null(FolderMove.LinkTarget(videos));
    }
}
