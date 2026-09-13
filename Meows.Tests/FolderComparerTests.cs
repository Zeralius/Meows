using Meows.Disk;
using Meows.Plugins.Purrge.Services;
using Meows.Plugins.Purrge.ViewModels;

namespace Meows.Tests;

/// <summary>
/// Is the copy really a copy. Two real folders on disk each time, because the thing being tested
/// is what the filesystem says, and a fake filesystem would only prove the fake agrees.
/// </summary>
public class FolderComparerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "meows-compare-" + Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly string _copy;

    public FolderComparerTests()
    {
        _source = Path.Combine(_root, "source");
        _copy = Path.Combine(_root, "copy");
        Directory.CreateDirectory(_source);
        Directory.CreateDirectory(_copy);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void Write(string side, string relative, string content, DateTime? modified = null)
    {
        var path = Path.Combine(side, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        if (modified is { } when)
            File.SetLastWriteTimeUtc(path, when);
    }

    private CompareReport Run(bool trust = false) =>
        new FolderComparer().Compare(_source, _copy, new CompareOptions(TrustTimestamps: trust), null, CancellationToken.None);

    private static readonly DateTime Noon = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_faithful_copy_has_nothing_to_report()
    {
        Write(_source, "a.txt", "alpha", Noon);
        Write(_source, "deep/b.txt", "bravo", Noon);
        Write(_copy, "a.txt", "alpha", Noon);
        Write(_copy, "deep/b.txt", "bravo", Noon);

        var report = Run();

        Assert.True(report.CopyIsGood);
        Assert.Empty(report.Findings);
        Assert.Equal(2, report.Checked);
        Assert.Equal(10, report.BytesChecked);
    }

    [Fact]
    public void Each_kind_of_wrong_is_told_apart()
    {
        Write(_source, "missing.txt", "gone from the copy", Noon);
        Write(_source, "stale.txt", "newer here", Noon);
        Write(_copy, "stale.txt", "older here", Noon.AddDays(-1));
        Write(_source, "different.txt", "same age, x", Noon);
        Write(_copy, "different.txt", "same age, y", Noon);
        Write(_copy, "extra.txt", "only over here", Noon);

        var report = Run();

        Assert.False(report.CopyIsGood);
        Assert.Equal(FindingKind.Missing, report.Findings.Single(f => f.RelativePath == "missing.txt").Kind);
        Assert.Equal(FindingKind.Stale, report.Findings.Single(f => f.RelativePath == "stale.txt").Kind);
        Assert.Equal(FindingKind.Different, report.Findings.Single(f => f.RelativePath == "different.txt").Kind);
        Assert.Equal(FindingKind.Extra, report.Findings.Single(f => f.RelativePath == "extra.txt").Kind);
    }

    [Fact]
    public void Missing_comes_first_and_extras_last()
    {
        Write(_copy, "extra.txt", "x", Noon);
        Write(_source, "b.txt", "bb", Noon);
        Write(_source, "a.txt", "a", Noon);

        var kinds = Run().Findings.Select(f => f.Kind).ToList();

        Assert.Equal([FindingKind.Missing, FindingKind.Missing, FindingKind.Extra], kinds);
    }

    [Fact]
    public void Same_size_and_date_but_different_bytes_is_caught_when_not_trusting()
    {
        // The quiet corruption case: nothing about the file's shape changed, only what is in it.
        Write(_source, "photo.bin", "ABCDEFGH", Noon);
        Write(_copy, "photo.bin", "ABCDEFGX", Noon);

        var thorough = Run(trust: false);
        var quick = Run(trust: true);

        Assert.Equal(FindingKind.Different, Assert.Single(thorough.Findings).Kind);
        Assert.Equal(1, thorough.ReadInFull);

        Assert.True(quick.CopyIsGood);
        Assert.Equal(0, quick.ReadInFull);
    }

    [Fact]
    public void Extras_do_not_make_the_copy_bad()
    {
        Write(_source, "a.txt", "a", Noon);
        Write(_copy, "a.txt", "a", Noon);
        Write(_copy, "leftover.txt", "from an earlier run", Noon);

        var report = Run();

        Assert.True(report.CopyIsGood);
        Assert.Equal(1, report.Count(FindingKind.Extra));
    }

    [Fact]
    public void Two_seconds_is_the_same_moment_and_ten_is_not()
    {
        Assert.True(FolderComparer.SameMoment(Noon, Noon.AddSeconds(2)));
        Assert.False(FolderComparer.SameMoment(Noon, Noon.AddSeconds(10)));
    }

    [Fact]
    public void A_folder_inside_the_other_cannot_be_compared_against_it()
    {
        Assert.True(CompareViewModel.SameOrNested(_source, Path.Combine(_source, "inner")));
        Assert.True(CompareViewModel.SameOrNested(_source, _source + Path.DirectorySeparatorChar));
        Assert.False(CompareViewModel.SameOrNested(_source, _copy));

        // A sibling whose name starts the same way is not nested.
        Assert.False(CompareViewModel.SameOrNested(Path.Combine(_root, "photos"), Path.Combine(_root, "photos-backup")));
    }

    [Fact]
    public void Sizes_settle_most_pairs_without_a_read()
    {
        Write(_source, "a.txt", "short", Noon);
        Write(_copy, "a.txt", "much longer", Noon);

        var report = Run();

        Assert.Equal(FindingKind.Different, Assert.Single(report.Findings).Kind);
        Assert.Equal(0, report.ReadInFull);
    }
}

public class DiskContentHashTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "meows-hash-" + Guid.NewGuid().ToString("N"));

    public DiskContentHashTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Same_reads_the_whole_file_only_when_the_head_agrees()
    {
        var a = Path.Combine(_root, "a");
        var b = Path.Combine(_root, "b");
        var c = Path.Combine(_root, "c");

        var head = new byte[ContentHash.PartialBytes + 10];
        Random.Shared.NextBytes(head);
        File.WriteAllBytes(a, head);
        File.WriteAllBytes(b, head);

        var tail = (byte[])head.Clone();
        tail[^1] ^= 0xFF;
        File.WriteAllBytes(c, tail);

        Assert.True(ContentHash.Same(a, b));
        Assert.False(ContentHash.Same(a, c));
        Assert.Equal(ContentHash.Partial(a), ContentHash.Partial(c));
    }

    [Fact]
    public void An_unreadable_file_is_no_answer_rather_than_a_wrong_one()
    {
        var a = Path.Combine(_root, "a");
        File.WriteAllText(a, "x");

        Assert.Null(ContentHash.Same(a, Path.Combine(_root, "does-not-exist")));
    }
}
