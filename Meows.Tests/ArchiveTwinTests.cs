using System.IO.Compression;
using Meows.Disk;

namespace Meows.Tests;

/// <summary>
/// An archive beside the folder it was extracted to. The rule under test is that "already
/// extracted" is a content check and never a name match.
/// </summary>
public sealed class ArchiveTwinTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "yarn-" + Guid.NewGuid().ToString("N")[..10]);

    public ArchiveTwinTests() => Directory.CreateDirectory(_root);

    private static readonly byte[] Big = Enumerable.Range(0, 200_000).Select(i => (byte)(i * 7)).ToArray();

    /// <summary>A folder of files, and a zip of exactly those files beside it, named to pair.</summary>
    private (string Archive, string Folder) Pair(string name, Dictionary<string, byte[]> files, string? insideRoot = null)
    {
        var folder = Path.Combine(_root, name);
        foreach (var (relative, bytes) in files)
        {
            var path = Path.Combine(folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }

        var archive = Path.Combine(_root, name + ".zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            foreach (var (relative, bytes) in files)
            {
                var entryName = (insideRoot is null ? "" : insideRoot + "/") + relative.Replace('\\', '/');
                using var stream = zip.CreateEntry(entryName).Open();
                stream.Write(bytes);
            }
        }

        return (archive, folder);
    }

    [Fact]
    public void The_same_files_on_both_sides_is_a_twin()
    {
        var (archive, folder) = Pair("photos", new()
        {
            ["a.jpg"] = [1, 2, 3],
            [@"sub\b.jpg"] = Big,
        });

        var report = Archives.Compare(archive, folder);

        Assert.Equal(TwinVerdict.Twin, report.Verdict);
        Assert.Equal(2, report.Matched);
        Assert.Equal(3 + Big.Length, report.MatchedBytes);
        Assert.True(report.ArchiveSize > 0);
    }

    [Fact]
    public void A_folder_with_the_right_name_and_different_bytes_is_not_a_twin()
    {
        var (archive, folder) = Pair("photos", new() { ["a.bin"] = Big });

        // Same size, one byte changed past the first 64 KB, so only the full read can tell.
        var altered = (byte[])Big.Clone();
        altered[150_000] ^= 0xFF;
        File.WriteAllBytes(Path.Combine(folder, "a.bin"), altered);

        var report = Archives.Compare(archive, folder);

        Assert.Equal(TwinVerdict.Differs, report.Verdict);
        Assert.Equal(1, report.Different);
        Assert.Equal(0, report.Matched);
    }

    [Fact]
    public void A_file_the_folder_lost_is_reported_as_missing()
    {
        var (archive, folder) = Pair("photos", new() { ["a.bin"] = [1], ["b.bin"] = [2] });
        File.Delete(Path.Combine(folder, "b.bin"));

        var report = Archives.Compare(archive, folder);

        Assert.Equal(TwinVerdict.Differs, report.Verdict);
        Assert.Equal(1, report.Missing);
        Assert.Equal(1, report.Matched);
    }

    [Fact]
    public void Extra_files_in_the_folder_keep_it_a_twin_but_say_so()
    {
        var (archive, folder) = Pair("photos", new() { ["a.bin"] = [1] });
        File.WriteAllBytes(Path.Combine(folder, "edited-later.bin"), [9, 9]);

        var report = Archives.Compare(archive, folder);

        // Removing the archive loses nothing; removing the folder loses the extra. The verdict
        // has to keep those two apart, because the advice is different for each.
        Assert.Equal(TwinVerdict.TwinWithExtras, report.Verdict);
        Assert.True(report.IsTwin);
        Assert.Equal(1, report.Extra);
    }

    [Fact]
    public void An_archive_with_one_top_folder_matches_however_it_was_extracted()
    {
        // Extract Here strips the top folder; Extract All keeps it. Both are the same contents.
        var (archive, stripped) = Pair("stripped", new() { ["a.bin"] = [1, 2] }, insideRoot: "stripped");
        Assert.Equal(TwinVerdict.Twin, Archives.Compare(archive, stripped).Verdict);

        var kept = Path.Combine(_root, "kept");
        Directory.CreateDirectory(Path.Combine(kept, "kept"));
        File.WriteAllBytes(Path.Combine(kept, "kept", "a.bin"), [1, 2]);
        var keptArchive = Path.Combine(_root, "kept.zip");
        using (var zip = ZipFile.Open(keptArchive, ZipArchiveMode.Create))
        {
            using var stream = zip.CreateEntry("kept/a.bin").Open();
            stream.Write([1, 2]);
        }

        Assert.Equal(TwinVerdict.Twin, Archives.Compare(keptArchive, kept).Verdict);
    }

    [Fact]
    public void An_archive_that_cannot_be_read_says_so_rather_than_no()
    {
        var folder = Path.Combine(_root, "stuff");
        Directory.CreateDirectory(folder);
        var rar = Path.Combine(_root, "stuff.rar");
        File.WriteAllBytes(rar, [0x52, 0x61, 0x72, 0x21]);

        Assert.Equal(TwinVerdict.Unreadable, Archives.Compare(rar, folder).Verdict);
        Assert.Null(Archives.Peek(rar));

        var broken = Path.Combine(_root, "broken.zip");
        File.WriteAllBytes(broken, [1, 2, 3, 4]);
        Directory.CreateDirectory(Path.Combine(_root, "broken"));
        Assert.Equal(TwinVerdict.Unreadable, Archives.Compare(broken, Path.Combine(_root, "broken")).Verdict);
    }

    [Fact]
    public void Siblings_are_found_from_either_side()
    {
        var (archive, folder) = Pair("comic", new() { ["p1.png"] = [1] });
        File.WriteAllBytes(Path.Combine(_root, "loner.zip"), [1]);

        Assert.Equal(folder, Archives.SiblingFolderOf(archive));
        Assert.Equal(archive, Archives.SiblingArchiveOf(folder));
        Assert.Null(Archives.SiblingFolderOf(Path.Combine(_root, "loner.zip")));

        // Double extensions come off as a pair.
        Assert.Equal("photos", Archives.Stem("photos.tar.gz"));
        Assert.Equal("photos", Archives.Stem("photos.cbz"));
        Assert.True(Archives.IsArchive("x.7z"));
        Assert.False(Archives.CanRead("x.7z"));
        Assert.False(Archives.IsArchive("x.jpg"));
    }

    [Fact]
    public void Peek_says_what_is_inside_without_a_folder_beside_it()
    {
        var archive = Path.Combine(_root, "alone.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "a.png", "b.png", "notes.txt" })
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(new byte[10]);
            }
        }

        var summary = Archives.Peek(archive)!;

        Assert.Equal(3, summary.EntryCount);
        Assert.Equal(30, summary.UnpackedSize);
        Assert.Equal(".png", summary.TopKind);
        Assert.Equal(66, summary.TopShare);
    }

    [Fact]
    public void The_inspector_reads_both_halves_as_the_same_pair()
    {
        var (archive, folder) = Pair("maps", new() { ["m.png"] = Big });

        var archiveSide = ArchiveInspector.Of(archive);
        var folderSide = FolderInspector.Of(folder);

        Assert.Equal(FolderVerdict.Twin, archiveSide.Verdict);
        Assert.Equal(FolderVerdict.Twin, folderSide.Verdict);
        Assert.Contains("maps", archiveSide.Headline);
        Assert.Contains("maps.zip", folderSide.Headline);
        Assert.NotNull(archiveSide.Twin);
        Assert.Equal(1, folderSide.Twin!.Matched);
        Assert.Contains(archiveSide.Evidence, line => line.Contains("paid for twice"));
    }

    [Fact]
    public void A_same_named_pair_that_differs_is_only_an_archive()
    {
        var (archive, folder) = Pair("maps", new() { ["m.png"] = [1, 2, 3] });
        File.WriteAllBytes(Path.Combine(folder, "m.png"), [3, 2, 1]);

        var archiveSide = ArchiveInspector.Of(archive);
        var folderSide = FolderInspector.Of(folder);

        Assert.Equal(FolderVerdict.Archive, archiveSide.Verdict);
        Assert.NotEqual(FolderVerdict.Twin, folderSide.Verdict);
        Assert.Contains(archiveSide.Evidence, line => line.Contains("not the same"));
        Assert.Contains(folderSide.Evidence, line => line.Contains("not the same"));
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
