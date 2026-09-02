using Meows.Plugins.Litter.Services;

namespace Meows.Tests;

public sealed class LitterScanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "litter-" + Guid.NewGuid().ToString("N")[..10]);
    private static readonly DateTime Now = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Local);

    public LitterScanTests() => Directory.CreateDirectory(_root);

    private string File(string name, int bytes = 100, int daysOld = 0)
    {
        var path = Path.Combine(_root, name);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        System.IO.File.SetLastWriteTime(path, Now.AddDays(-daysOld));
        return path;
    }

    [Fact]
    public void What_a_download_is_comes_from_its_extension()
    {
        Assert.Equal(LitterKind.Installer, LitterScan.KindOf("setup.exe"));
        Assert.Equal(LitterKind.Archive, LitterScan.KindOf("pack.7z"));
        Assert.Equal(LitterKind.Image, LitterScan.KindOf("art.png"));
        Assert.Equal(LitterKind.Video, LitterScan.KindOf("clip.mp4"));
        Assert.Equal(LitterKind.Document, LitterScan.KindOf("manual.pdf"));
        Assert.Equal(LitterKind.Other, LitterScan.KindOf("mystery.qqq"));
    }

    [Fact]
    public void A_download_that_never_finished_gets_its_own_category()
    {
        // These are always junk, which is why they are worth separating from everything else.
        Assert.Equal(LitterKind.Unfinished, LitterScan.KindOf("film.mkv.crdownload"));
        Assert.Equal(LitterKind.Unfinished, LitterScan.KindOf("big.iso.part"));
    }

    [Fact]
    public void Age_falls_into_the_bucket_you_would_say_out_loud()
    {
        Assert.Equal(LitterAge.Today, LitterScan.AgeOf(Now.AddHours(-3), Now));
        Assert.Equal(LitterAge.ThisWeek, LitterScan.AgeOf(Now.AddDays(-3), Now));
        Assert.Equal(LitterAge.ThisMonth, LitterScan.AgeOf(Now.AddDays(-20), Now));
        Assert.Equal(LitterAge.Older, LitterScan.AgeOf(Now.AddDays(-200), Now));
    }

    [Fact]
    public void Everything_in_the_folder_is_read_with_its_size_and_age()
    {
        File("new.png", 500, daysOld: 0);
        File("old.exe", 1500, daysOld: 90);

        var items = LitterScan.Read(_root, Now);

        Assert.Equal(2, items.Count);
        var old = items.Single(i => i.Name == "old.exe");
        Assert.Equal(1500, old.Size);
        Assert.Equal(LitterKind.Installer, old.Kind);
        Assert.Equal(LitterAge.Older, old.Age);
        Assert.Equal(90, old.Days);
        // Measured against the injected clock rather than the real one. This assertion used to
        // read DateTime.Now through the item and so passed only on the day it was written.
        Assert.Equal(91, LitterScan.DaysOf(Now.AddDays(-90), Now.AddDays(1)));
    }

    [Fact]
    public void A_folder_counts_as_one_thing_and_carries_what_is_inside_it()
    {
        var extracted = Path.Combine(_root, "extracted");
        Directory.CreateDirectory(Path.Combine(extracted, "deep"));
        System.IO.File.WriteAllBytes(Path.Combine(extracted, "a.bin"), new byte[400]);
        System.IO.File.WriteAllBytes(Path.Combine(extracted, "deep", "b.bin"), new byte[600]);

        var items = LitterScan.Read(_root, Now);

        // One row, not three, and its size is what removing it would actually free.
        var folder = Assert.Single(items);
        Assert.Equal("extracted", folder.Name);
        Assert.Equal(1000, folder.Size);
    }

    [Fact]
    public void Only_the_top_level_is_listed()
    {
        var extracted = Path.Combine(_root, "extracted");
        Directory.CreateDirectory(extracted);
        System.IO.File.WriteAllBytes(Path.Combine(extracted, "buried.png"), new byte[100]);
        File("loose.png");

        var items = LitterScan.Read(_root, Now);

        Assert.Equal(2, items.Count);
        Assert.DoesNotContain(items, i => i.Name == "buried.png");
    }

    [Fact]
    public void A_folder_that_is_not_there_is_empty_rather_than_a_crash()
    {
        Assert.Empty(LitterScan.Read(Path.Combine(_root, "nope"), Now));
    }

    [Fact]
    public void Sizes_are_written_for_people()
    {
        Assert.Equal("512 B", LitterScan.Humanise(512));
        Assert.Equal("2 KB", LitterScan.Humanise(2048));
        Assert.EndsWith("MB", LitterScan.Humanise(5 * 1024 * 1024));
        Assert.EndsWith("GB", LitterScan.Humanise(3L * 1024 * 1024 * 1024));
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
