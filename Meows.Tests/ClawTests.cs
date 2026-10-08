using System.IO.Compression;
using Meows.Disk;
using Meows.Plugins.Chonk.ViewModels;

namespace Meows.Tests;

/// <summary>
/// Chonk's Extract and check: the cost said first, nothing overwritten, a half-done unpacking
/// never left behind, an entry that climbs out refused outright, and the result held up against
/// the archive with the same comparison that finds twins.
/// </summary>
public sealed class ClawTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "meows-claw-" + Guid.NewGuid().ToString("N")[..8]);

    public ClawTests() => Directory.CreateDirectory(_root);

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

    private string Zip(string name, params (string Entry, byte[] Bytes)[] entries)
    {
        var path = Path.Combine(_root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, bytes) in entries)
        {
            using var stream = zip.CreateEntry(entry).Open();
            stream.Write(bytes);
        }
        return path;
    }

    private string Photos() => Zip("photos.zip",
        ("a.jpg", new byte[1200]),
        ("sub/b.jpg", new byte[3400]),
        ("sub/deeper/c.txt", "hello"u8.ToArray()));

    [Fact]
    public void The_plan_says_where_and_how_much_before_anything_is_written()
    {
        var archive = Photos();

        var plan = ArchiveExtractor.Plan(archive, _ => 10_000_000);

        Assert.True(plan.CanGo);
        Assert.Equal(Path.Combine(_root, "photos"), plan.Folder);
        Assert.Equal(3, plan.Files);
        Assert.Equal(1200 + 3400 + 5, plan.Bytes);
        Assert.Equal(10_000_000, plan.Free);
        Assert.False(Directory.Exists(plan.Folder));
    }

    [Fact]
    public void A_folder_already_there_no_room_nothing_inside_or_not_a_zip_is_refused()
    {
        var archive = Photos();
        Assert.Equal(ArchiveExtractor.Refusal.NoRoom, ArchiveExtractor.Plan(archive, _ => 1000).Refusal);

        Directory.CreateDirectory(Path.Combine(_root, "photos"));
        Assert.Equal(ArchiveExtractor.Refusal.FolderThere, ArchiveExtractor.Plan(archive).Refusal);

        Assert.Equal(ArchiveExtractor.Refusal.Empty, ArchiveExtractor.Plan(Zip("empty.zip")).Refusal);

        var rar = Path.Combine(_root, "old.rar");
        File.WriteAllBytes(rar, [.. "Rar!\x1A\x07\x00"u8]);
        Assert.Equal(ArchiveExtractor.Refusal.NotZip, ArchiveExtractor.Plan(rar).Refusal);

        var broken = Path.Combine(_root, "broken.zip");
        File.WriteAllText(broken, "not a zip at all");
        Assert.Equal(ArchiveExtractor.Refusal.Unreadable, ArchiveExtractor.Plan(broken).Refusal);
    }

    [Fact]
    public void An_archive_that_wants_a_password_is_refused()
    {
        var archive = Zip("locked.zip", ("secret.txt", "x"u8.ToArray()));

        // Set the encrypted flag on the entry the way a password-protecting tool does: bit 0 of
        // the general purpose flags, in the local header and in the central directory both.
        var bytes = File.ReadAllBytes(archive);
        for (var i = 0; i + 8 < bytes.Length; i++)
        {
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 0x03 && bytes[i + 3] == 0x04)
                bytes[i + 6] |= 1;
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 0x01 && bytes[i + 3] == 0x02)
                bytes[i + 8] |= 1;
        }
        File.WriteAllBytes(archive, bytes);

        Assert.Equal(ArchiveExtractor.Refusal.Password, ArchiveExtractor.Plan(archive).Refusal);
    }

    [Fact]
    public void Unpacked_and_checked_the_archive_is_a_twin_and_nothing_temporary_is_left()
    {
        var archive = Photos();
        var before = File.ReadAllBytes(archive);
        var seen = new List<(int, int)>();

        var report = ArchiveExtractor.Extract(ArchiveExtractor.Plan(archive), new Sync(seen.Add), CancellationToken.None);

        Assert.True(report.Ok, report.Error);
        Assert.True(report.Verified);
        Assert.Equal(3, report.Files);
        Assert.Equal(TwinVerdict.Twin, report.Check!.Verdict);
        Assert.Equal(3400, new FileInfo(Path.Combine(_root, "photos", "sub", "b.jpg")).Length);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(_root, "photos", "sub", "deeper", "c.txt")));
        Assert.False(Directory.Exists(Path.Combine(_root, "photos" + ArchiveExtractor.PartSuffix)));
        Assert.Equal(before, File.ReadAllBytes(archive));
        Assert.Equal([(1, 3), (2, 3), (3, 3)], seen);
    }

    [Fact]
    public void An_entry_that_climbs_out_stops_everything_and_leaves_nothing()
    {
        var archive = Zip("evil.zip", ("fine.txt", "ok"u8.ToArray()), ("../escaped.txt", "no"u8.ToArray()));

        var report = ArchiveExtractor.Extract(ArchiveExtractor.Plan(archive), null, CancellationToken.None);

        Assert.False(report.Ok);
        Assert.Null(report.Check);
        Assert.Contains("escaped.txt", report.Error);
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
        Assert.False(Directory.Exists(Path.Combine(_root, "evil")));
        Assert.False(Directory.Exists(Path.Combine(_root, "evil" + ArchiveExtractor.PartSuffix)));
    }

    [Fact]
    public void Called_off_it_takes_the_half_made_folder_away()
    {
        var archive = Photos();
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => ArchiveExtractor.Extract(ArchiveExtractor.Plan(archive), null, stop.Token));

        Assert.False(Directory.Exists(Path.Combine(_root, "photos")));
        Assert.False(Directory.Exists(Path.Combine(_root, "photos" + ArchiveExtractor.PartSuffix)));
    }

    [Fact]
    public async Task Chonk_offers_it_on_a_zip_asks_with_the_cost_and_refuses_when_the_folder_is_there()
    {
        var archive = Photos();
        var host = new FakeHost(Path.Combine(_root, "host"));
        using var model = new ChonkViewModel(host);
        model.ShowScanned(DiskScan.Run(_root, new ScanOptions { ListFilesFrom = 0 }, null, CancellationToken.None));

        model.Selected = model.Entries.First(e => e.Path == archive);
        Assert.True(model.CanExtract);
        model.ExtractCommand.Execute(null);
        await Task.Delay(200);

        Assert.True(model.IsAskingExtract);
        Assert.Contains("3 files", model.ExtractDetail);
        Assert.Contains("photos", model.ExtractDetail);

        model.ConfirmExtractCommand.Execute(null);
        Assert.False(model.IsAskingExtract);
        Assert.Contains(host.Work.Requested, t => t.Contains("photos.zip"));

        // One unpacking at a time: while that one is under way the button is off.
        Assert.False(model.CanExtract);

        // The fake host runs no work; unpack by hand and ask again from a fresh tab.
        ArchiveExtractor.Extract(ArchiveExtractor.Plan(archive), null, CancellationToken.None);
        using var again = new ChonkViewModel(new FakeHost(Path.Combine(_root, "host-again")));
        again.ShowScanned(DiskScan.Run(_root, new ScanOptions { ListFilesFrom = 0 }, null, CancellationToken.None));
        again.Selected = again.Entries.First(e => e.Path == archive);
        again.ExtractCommand.Execute(null);
        await Task.Delay(200);
        Assert.False(again.IsAskingExtract);
        Assert.Contains("already beside it", again.ErrorMessage);
    }

    private sealed class Sync(Action<(int, int)> report) : IProgress<(int Done, int Total)>
    {
        public void Report((int Done, int Total) value) => report(value);
    }
}
