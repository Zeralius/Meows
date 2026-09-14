using Meows.Plugins.Abstractions;
using Meows.Plugins.Catnip.Services;
using Meows.Plugins.Catnip.ViewModels;

namespace Meows.Tests;

/// <summary>
/// Catnip: the walk reads the last-access stamp without moving it, "never opened" means not
/// opened since it arrived, and the tab lists the least recently touched first.
/// </summary>
public sealed class CatnipTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "catnip-" + Guid.NewGuid().ToString("N")[..10]);

    public CatnipTests() => Directory.CreateDirectory(_root);

    /// <summary>A file with its three stamps set by hand, the way a download or a copy would leave them.</summary>
    private string Stamped(string name, int bytes, DateTime created, DateTime written, DateTime accessed)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetCreationTime(path, created);
        File.SetLastWriteTime(path, written);
        File.SetLastAccessTime(path, accessed);
        return path;
    }

    private static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0);

    [Fact]
    public void The_walk_reads_the_stamps_and_leaves_them_where_they_were()
    {
        var arrived = Now.AddMonths(-7);
        var never = Stamped("never.zip", 5000, arrived, arrived.AddMinutes(-2), arrived);
        var opened = Stamped("opened.pdf", 5000, arrived, arrived, Now.AddDays(-3));
        Stamped("tiny.txt", 10, arrived, arrived, arrived);

        var report = Neglect.Scan([_root], skipSystemFolders: true, minBytes: 1000, null, CancellationToken.None);

        Assert.Equal(2, report.Files.Count);
        Assert.Equal(1, report.Folders);
        var n = report.Files.Single(f => f.Path == never);
        var o = report.Files.Single(f => f.Path == opened);
        Assert.True(n.NeverOpened);
        Assert.False(o.NeverOpened);
        Assert.Equal(arrived, n.Arrived);
        // Reading the stamps is not an access.
        Assert.Equal(arrived, File.GetLastAccessTime(never));
    }

    [Fact]
    public void A_copy_arrives_when_it_was_created_not_when_it_was_written()
    {
        // Copied last month from a file written two years ago, opened once inside the hour of copying.
        var copied = new DateTime(2026, 8, 10, 9, 0, 0);
        var file = new NeglectedFile(@"F:\x.iso", 1, Created: copied, Written: copied.AddYears(-2), Accessed: copied.AddMinutes(40));

        Assert.Equal(copied, file.Arrived);
        Assert.True(file.NeverOpened);
        Assert.False((file with { Accessed = copied.AddHours(2) }).NeverOpened);
    }

    [Fact]
    public void Sifting_keeps_what_is_untouched_for_the_window_least_recently_touched_first()
    {
        var files = new List<NeglectedFile>
        {
            new(@"F:\a", 10, Now.AddDays(-400), Now.AddDays(-400), Now.AddDays(-400)),
            new(@"F:\b", 20, Now.AddDays(-200), Now.AddDays(-200), Now.AddDays(-10)),
            new(@"F:\c", 30, Now.AddDays(-200), Now.AddDays(-200), Now.AddDays(-150)),
            new(@"F:\d", 40, Now.AddDays(-100), Now.AddDays(-100), Now.AddDays(-100)),
        };

        Assert.Equal([@"F:\a", @"F:\d"], Neglect.Sift(files, onlyNeverOpened: true, 90, Now).Select(f => f.Path));
        Assert.Equal([@"F:\a", @"F:\c", @"F:\d"], Neglect.Sift(files, onlyNeverOpened: false, 90, Now).Select(f => f.Path));
        Assert.Equal([@"F:\a"], Neglect.Sift(files, onlyNeverOpened: false, 365, Now).Select(f => f.Path));
    }

    [Theory]
    [InlineData(0, "today")]
    [InlineData(5, "5 days ago")]
    [InlineData(30, "4 weeks ago")]
    [InlineData(200, "6 months ago")]
    [InlineData(800, "2 years ago")]
    public void How_long_ago_reads_in_the_unit_a_person_would_use(int days, string expected)
    {
        var text = TestStrings.Load();
        Assert.Equal(expected, Neglect.Ago(Now.AddDays(-days), Now, k => text[k]));
    }

    [Fact]
    public void A_handoff_adds_the_folder_and_asks_for_the_walk()
    {
        var host = new FakeHost(Path.Combine(_root, "hostdata"));
        var pile = Path.Combine(_root, "pile");
        var arrived = DateTime.Now.AddMonths(-6);
        Stamped(Path.Combine("pile", "big.zip"), 3_000_000, arrived, arrived, arrived);
        Stamped(Path.Combine("pile", "old.pdf"), 2_000_000, arrived.AddMonths(-3), arrived.AddMonths(-3), arrived.AddMonths(-3));
        host.SaveSettings(new CatnipSettings { Roots = [Path.Combine(_root, "elsewhere")], MinMegabytes = 1 });
        using var model = new CatnipViewModel(host);
        Assert.Single(model.Roots);

        Assert.True(model.Accepts(Handoff.Folder(pile)));
        model.Receive(Handoff.Folder(pile));

        Assert.Equal(2, model.Roots.Count);
        // The fake background does not run the walk; nothing is listed yet, but the task was asked for.
        Assert.Contains(host.Work.Requested, t => t == "Walking for the never opened");
        Assert.False(model.HasScanned);
    }

    [Fact]
    public void The_tab_lists_the_least_recently_touched_first_and_the_dials_narrow_it()
    {
        var host = new FakeHost(Path.Combine(_root, "hostdata2"));
        var arrived = DateTime.Now.AddMonths(-6);
        var big = Stamped(Path.Combine("pile2", "big.zip"), 3_000_000, arrived, arrived, arrived);
        var old = Stamped(Path.Combine("pile2", "old.pdf"), 2_000_000, arrived.AddMonths(-3), arrived.AddMonths(-3), arrived.AddMonths(-3));
        host.SaveSettings(new CatnipSettings { Roots = [Path.Combine(_root, "pile2")], MinMegabytes = 1 });
        using var model = new CatnipViewModel(host);

        // The fake background runs nothing, so the walk is run here and shown the way the task would.
        model.ShowWalked(Neglect.Scan(model.Roots.Select(r => r.Path).ToList(), true, 1_000_000, null, CancellationToken.None));

        Assert.True(model.HasScanned);
        Assert.Equal([old, big], model.Files.Select(f => f.Path));
        Assert.Contains("2 files", model.Headline);
        Assert.Contains("never opened", model.Files[0].WhenText);
        Assert.Contains(host.Store.Events, e => e.Kind == "scan");

        // Narrowing the window drops the newer one.
        model.OlderThanDays = 250;
        Assert.Equal([old], model.Files.Select(f => f.Path));
        model.OlderThanDays = 90;

        var hit = Assert.Single(model.Search("big", 5));
        hit.Open();
        Assert.Equal(big, model.Selected?.Path);
    }

    [Fact]
    public void Several_rows_can_be_picked_and_asked_about_together()
    {
        var host = new FakeHost(Path.Combine(_root, "hostdata3"));
        host.Handoffs.Reachable.Add(KnownPlugins.Purrge);
        var arrived = DateTime.Now.AddMonths(-6);
        var a = Stamped(Path.Combine("pile3", "a.zip"), 3_000_000, arrived, arrived, arrived);
        var b = Stamped(Path.Combine("pile3", "b.zip"), 2_000_000, arrived, arrived, arrived);
        var c = Stamped(Path.Combine("pile3", "c.zip"), 1_500_000, arrived, arrived, arrived);
        host.SaveSettings(new CatnipSettings { Roots = [Path.Combine(_root, "pile3")], MinMegabytes = 1 });
        using var model = new CatnipViewModel(host);
        model.ShowWalked(Neglect.Scan(model.Roots.Select(r => r.Path).ToList(), true, 1_000_000, null, CancellationToken.None));

        model.Selection.Select(0);
        model.Selection.Select(2);

        Assert.Equal(2, model.ChosenCount);
        Assert.Equal([a, c], model.Chosen.Select(f => f.Path));
        Assert.StartsWith("2 files, 4", model.ChosenText);
        Assert.Same(model.Files[0], model.Selected);

        model.AskPurrgeCommand.Execute(null);
        var (to, what) = Assert.Single(host.Handoffs.Sent);
        Assert.Equal(KnownPlugins.Purrge, to);
        Assert.Equal(HandoffVerbs.Files, what.Verb);
        Assert.Equal([a, c], what.Paths);
        Assert.True(what.WantsReply);
        what.Answer("1 of 2 have another copy");
        Assert.Equal("Purrge: 1 of 2 have another copy", model.Status);

        // Picking one row again makes it the only one.
        model.Selected = model.Files[1];
        Assert.Equal(1, model.ChosenCount);
        Assert.Equal(b, model.Selected?.Path);
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
