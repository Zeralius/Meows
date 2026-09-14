using Meows.Plugins.Purrge.Services;

namespace Meows.Tests;

public sealed class DuplicateScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "purrge-tests-" + Guid.NewGuid().ToString("N")[..10]);

    public DuplicateScannerTests() => Directory.CreateDirectory(Path.Combine(_root, "sub"));

    private string Write(string relative, byte[] content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static byte[] Payload(int seed, int size = 8192)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private Task<IReadOnlyList<DuplicateSet>> Scan(ScanOptions? options = null) =>
        new DuplicateScanner().ScanAsync(_root, options ?? new ScanOptions(), null, CancellationToken.None);

    [Fact]
    public async Task Asked_about_particular_files_only_their_sizes_are_opened_and_only_their_sets_come_back()
    {
        var asked = Write("asked.bin", Payload(1));
        Write("sub/copy-of-asked.bin", Payload(1));
        // A different pair, same size as each other, not asked about: a duplicate the whole-folder scan would list.
        Write("a.bin", Payload(2));
        Write("b.bin", Payload(2));
        // A small asked file, under the usual floor: still counted, because it was asked about.
        var small = Write("small.bin", Payload(3, 100));
        Write("sub/small-copy.bin", Payload(3, 100));
        var alone = Write("alone.bin", Payload(4, 5000));

        var sets = await new DuplicateScanner().ScanAsync([_root], new ScanOptions(OnlyCopiesOf: [asked, small, alone]), null, CancellationToken.None);

        Assert.Equal(2, sets.Count);
        Assert.Contains(sets, set => set.Files.Any(f => f.Path == asked) && set.Files.Count == 2);
        Assert.Contains(sets, set => set.Files.Any(f => f.Path == small) && set.Files.Count == 2);
        Assert.DoesNotContain(sets, set => set.Files.Any(f => f.Path.EndsWith("a.bin")));
    }

    [Fact]
    public async Task Identical_files_are_grouped_across_folders()
    {
        var payload = Payload(1);
        Write("original.bin", payload);
        Write("copy.bin", payload);
        Write("sub/deep.bin", payload);

        var sets = await Scan();

        Assert.Single(sets);
        Assert.Equal(3, sets[0].Files.Count);
    }

    [Fact]
    public async Task Same_size_but_different_bytes_is_not_a_duplicate()
    {
        Write("a.bin", Payload(1));
        Write("b.bin", Payload(2));

        Assert.Empty(await Scan());
    }

    [Fact]
    public async Task Files_below_the_size_floor_are_ignored()
    {
        Write("tiny-a.txt", "x"u8.ToArray());
        Write("tiny-b.txt", "x"u8.ToArray());

        Assert.Empty(await Scan());
    }

    [Fact]
    public async Task Lowering_the_floor_finds_the_small_duplicates()
    {
        Write("tiny-a.txt", "hello"u8.ToArray());
        Write("tiny-b.txt", "hello"u8.ToArray());

        var sets = await Scan(new ScanOptions(MinimumBytes: 1));

        Assert.Single(sets);
    }

    [Fact]
    public async Task Recoverable_space_is_size_times_copies_beyond_the_first()
    {
        var payload = Payload(3, 4096);
        Write("a.bin", payload);
        Write("b.bin", payload);
        Write("c.bin", payload);

        var set = Assert.Single(await Scan());

        Assert.Equal(4096, set.Size);
        Assert.Equal(4096 * 2, set.RedundantBytes);
    }

    [Fact]
    public async Task A_unique_file_produces_no_set()
    {
        Write("only.bin", Payload(4));

        Assert.Empty(await Scan());
    }

    [Fact]
    public async Task Cancelling_stops_the_scan()
    {
        Write("a.bin", Payload(5));
        Write("b.bin", Payload(5));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DuplicateScanner().ScanAsync(_root, new ScanOptions(), null, cts.Token));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // Best effort cleanup.
        }
    }
}
