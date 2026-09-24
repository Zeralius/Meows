using System.IO.Compression;
using Meows.Disk;

namespace Meows.Tests;

/// <summary>
/// Twine: an archive inside an archive, and an archive round a single file, both read off the
/// table of contents Chonk already peeks at, both said and never acted on.
/// </summary>
public sealed class TwineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "twine-" + Guid.NewGuid().ToString("N")[..10]);

    public TwineTests() => Directory.CreateDirectory(_root);

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

    private static byte[] Bytes(int count) => Enumerable.Range(0, count).Select(i => (byte)i).ToArray();

    [Fact]
    public void Archives_inside_are_found_by_name_with_their_size()
    {
        var path = Zip("download.zip",
            ("readme.txt", Bytes(10)),
            ("inner/part1.rar", Bytes(3000)),
            ("inner/part2.7z", Bytes(2000)));

        var summary = Archives.Peek(path)!;

        Assert.Equal(["inner/part1.rar", "inner/part2.7z"], summary.Nested.Select(n => n.Name));
        Assert.Equal(5000, summary.NestedBytes);
        Assert.Null(summary.OnlyEntry);

        var line = Assert.Single(ArchiveInspector.Wound(path, summary));
        Assert.Contains("2 other archives", line);
        Assert.Contains(ArchiveInspector.Of(path).Evidence, e => e == line);
    }

    [Fact]
    public void One_file_alone_is_a_wrapper_and_one_page_of_a_comic_is_said_softly()
    {
        var wrapper = Zip("song.zip", ("music/song.flac", Bytes(100)));
        var comic = Zip("strip.cbz", ("page01.png", Bytes(100)));
        var twice = Zip("twice.zip", ("again.zip", Bytes(100)));

        Assert.Equal("music/song.flac", Archives.Peek(wrapper)!.OnlyEntry);
        Assert.Contains("wrapper an upload tool", Assert.Single(ArchiveInspector.Wound(wrapper, Archives.Peek(wrapper)!)));
        Assert.Contains("single page", Assert.Single(ArchiveInspector.Wound(comic, Archives.Peek(comic)!)));

        // An archive round nothing but another archive is said once, as the wrapper it is.
        var line = Assert.Single(ArchiveInspector.Wound(twice, Archives.Peek(twice)!));
        Assert.Contains("wrapper round a wrapper", line);
    }

    [Fact]
    public void An_ordinary_archive_says_nothing_more()
    {
        var path = Zip("photos.zip", ("a.jpg", Bytes(10)), ("b.jpg", Bytes(10)));

        Assert.Empty(ArchiveInspector.Wound(path, Archives.Peek(path)!));
        Assert.Single(ArchiveInspector.Of(path).Evidence);
    }
}
