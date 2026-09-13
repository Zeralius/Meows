using Meows.Disk;

namespace Meows.Tests;

/// <summary>
/// A scanner reading a file is not the file being used, and the last-access stamp has to say so
/// afterwards, or one Purrge sweep flattens the history Catnip wants to read.
/// </summary>
public sealed class AccessTimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "access-" + Guid.NewGuid().ToString("N")[..10]);
    private static readonly DateTime LongAgo = new(2023, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    public AccessTimeTests() => Directory.CreateDirectory(_root);

    private string Old(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        File.SetLastAccessTimeUtc(path, LongAgo);
        return path;
    }

    [Fact]
    public void Hashing_a_file_leaves_its_last_access_where_it_was()
    {
        var path = Old("a.bin", Enumerable.Range(0, 200_000).Select(i => (byte)i).ToArray());

        Assert.NotNull(ContentHash.Partial(path));
        Assert.NotNull(ContentHash.Full(path));
        Assert.NotNull(ContentHash.Same(path, path));

        Assert.Equal(LongAgo, File.GetLastAccessTimeUtc(path));
    }

    [Fact]
    public void Working_out_what_a_folder_is_leaves_its_files_alone()
    {
        var folder = Path.Combine(_root, "obj");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "x.dll");
        File.WriteAllBytes(path, new byte[16]);
        File.SetLastAccessTimeUtc(path, LongAgo);

        FolderInspector.Of(folder);

        Assert.Equal(LongAgo, File.GetLastAccessTimeUtc(path));
    }

    [Fact]
    public void Looking_inside_an_archive_leaves_it_alone()
    {
        var archive = Path.Combine(_root, "z.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(archive, System.IO.Compression.ZipArchiveMode.Create))
        {
            using var s = zip.CreateEntry("a.png").Open();
            s.Write(new byte[10]);
        }
        File.SetLastAccessTimeUtc(archive, LongAgo);

        Assert.NotNull(Archives.Peek(archive));
        Directory.CreateDirectory(Path.Combine(_root, "z"));
        Archives.Compare(archive, Path.Combine(_root, "z"));

        Assert.Equal(LongAgo, File.GetLastAccessTimeUtc(archive));
    }

    [Fact]
    public void A_file_that_cannot_be_read_is_not_a_problem_for_the_stamp()
    {
        Assert.Null(AccessTime.Remember(Path.Combine(_root, "missing.bin")));
        AccessTime.Restore(Path.Combine(_root, "missing.bin"), LongAgo);
        Assert.Equal(7, AccessTime.Preserving(Path.Combine(_root, "missing.bin"), () => 7));
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
