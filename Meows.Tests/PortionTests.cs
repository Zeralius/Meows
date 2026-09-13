using System.IO.Compression;
using Meows.Bot;
using Meows.Media;
using Meows.Plugins.Portion.Services;
using SkiaSharp;

namespace Meows.Tests;

/// <summary>
/// What is too big to post, and making it smaller. The Bot API's limits are the fixed points
/// here; the files are built to sit just either side of them.
/// </summary>
public class PortionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "meows-portion-" + Guid.NewGuid().ToString("N"));
    private readonly GroupConfig _group = new() { Name = "Paws", Folder = "groups/paws" };

    public PortionTests()
    {
        Directory.CreateDirectory(_root);

        // Plain deletion here. The app sends originals to the Recycle Bin, and a test suite
        // that filled the bin with noise pictures would not be thanked for it.
        Slimmer.RemoveOriginal = path =>
        {
            File.Delete(path);
            return null;
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Noise does not compress, which is how a picture is made to weigh what a test needs.</summary>
    private static byte[] NoisyPng(int side)
    {
        using var bitmap = new SKBitmap(side, side);
        var random = new Random(3);
        for (var y = 0; y < side; y++)
        for (var x = 0; x < side; x++)
            bitmap.SetPixel(x, y, new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
        return Preparer.Encode(bitmap, ImageFormat.Png, 100);
    }

    [Fact]
    public void A_small_picture_is_not_reported()
    {
        var path = Write("fine.jpg", TestPictures.Jpeg(400, 300));

        Assert.Null(Weigher.Inspect(_group, path));
    }

    [Fact]
    public void A_picture_over_the_photo_limit_is_reported_and_can_be_shrunk()
    {
        var bytes = NoisyPng(2000);
        Assert.True(bytes.LongLength > MediaRules.PhotoLimitBytes, $"test picture is only {bytes.Length} bytes");
        var path = Write("big.png", bytes);

        var heavy = Weigher.Inspect(_group, path);

        Assert.NotNull(heavy);
        Assert.Contains(Trouble.OverBytes, heavy.Troubles);
        Assert.Equal(MediaKind.Photo, heavy.Kind);
        Assert.True(heavy.CanShrink);
        Assert.Equal(2000, heavy.Width);
    }

    [Fact]
    public void A_picture_too_wide_and_tall_for_sendphoto_is_reported_by_its_header_alone()
    {
        // 6000 by 4200 is 10200 across, over the sum, even though a flat picture this size is tiny.
        var path = Write("huge.jpg", TestPictures.Jpeg(6000, 4200));

        var heavy = Weigher.Inspect(_group, path);

        Assert.NotNull(heavy);
        Assert.Equal([Trouble.TooManyPixels], heavy.Troubles);
        Assert.True(heavy.CanShrink);
    }

    [Fact]
    public void An_odd_ratio_is_reported_and_left_alone()
    {
        var path = Write("banner.jpg", TestPictures.Jpeg(2100, 100));

        var heavy = Weigher.Inspect(_group, path);

        Assert.NotNull(heavy);
        Assert.Contains(Trouble.OddRatio, heavy.Troubles);
        Assert.False(heavy.CanShrink);
    }

    [Fact]
    public void Shrinking_keeps_the_name_the_place_in_the_queue_and_gets_under_the_limit()
    {
        var path = Write("big.png", NoisyPng(2000));
        var when = new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, when);
        var heavy = Weigher.Inspect(_group, path)!;

        var outcome = Slimmer.Shrink(heavy);

        Assert.True(outcome.Ok, outcome.Error);
        Assert.NotNull(outcome.Path);
        Assert.True(outcome.After <= MediaRules.PhotoLimitBytes);
        Assert.True(outcome.After < outcome.Before);

        // Same stem, whatever the format had to become, and the modified time carried over so
        // the bot's queue order does not change.
        Assert.Equal("big", Path.GetFileNameWithoutExtension(outcome.Path));
        Assert.Equal(when, File.GetLastWriteTimeUtc(outcome.Path));
        Assert.Null(Weigher.Inspect(_group, outcome.Path));
        Assert.False(File.Exists(path + ".portion"));
    }

    [Fact]
    public void A_comic_with_a_heavy_page_is_rebuilt_with_the_same_pages()
    {
        var archive = Path.Combine(_root, "comic.cbz");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            Add(zip, "01.jpg", TestPictures.Jpeg(400, 600));
            Add(zip, "02.png", NoisyPng(2000));
            Add(zip, "03.jpg", TestPictures.Jpeg(400, 600));
            Add(zip, "notes.txt", "not a page"u8.ToArray());
        }

        var heavy = Weigher.Inspect(_group, archive);
        Assert.NotNull(heavy);
        Assert.Equal(MediaKind.Comic, heavy.Kind);
        Assert.Equal(3, heavy.Pages);
        Assert.Equal(1, heavy.HeavyPageCount);

        var outcome = Slimmer.Shrink(heavy);

        Assert.True(outcome.Ok, outcome.Error);

        // Still listed, but only as a note about the text file inside; nothing fails any more.
        var after = Weigher.Inspect(_group, archive);
        Assert.NotNull(after);
        Assert.Equal([Trouble.ForeignFiles], after.Troubles);
        Assert.False(after.WillFail);

        using var rebuilt = ZipFile.OpenRead(archive);
        // The heavy page became a JPEG, so its name says so; it still sorts between 01 and 03.
        Assert.Equal(["01.jpg", "02.jpg", "03.jpg", "notes.txt"], rebuilt.Entries.Select(e => e.FullName));
        Assert.True(rebuilt.GetEntry("02.jpg")!.Length <= MediaRules.PhotoLimitBytes);
        Assert.Equal("not a page", new StreamReader(rebuilt.GetEntry("notes.txt")!.Open()).ReadToEnd());
    }

    [Fact]
    public void A_comic_with_nothing_postable_inside_fails_and_says_so()
    {
        var archive = Path.Combine(_root, "empty.cbz");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            Add(zip, "readme.txt", "just words"u8.ToArray());
            Add(zip, "cover.gif", TestPictures.Jpeg());
        }

        var heavy = Weigher.Inspect(_group, archive);

        Assert.NotNull(heavy);
        Assert.Contains(Trouble.EmptyComic, heavy.Troubles);
        Assert.Contains(Trouble.ForeignFiles, heavy.Troubles);
        Assert.True(heavy.WillFail);
        Assert.False(heavy.CanShrink);
    }

    [Fact]
    public void A_page_that_is_not_a_picture_inside_is_caught_by_its_bytes()
    {
        var archive = Path.Combine(_root, "liar.cbz");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            Add(zip, "01.jpg", TestPictures.Jpeg());
            Add(zip, "02.jpg", "<html>this is a saved error page</html>"u8.ToArray());
        }

        var heavy = Weigher.Inspect(_group, archive);

        Assert.NotNull(heavy);
        Assert.Equal([Trouble.BadPages], heavy.Troubles);
        Assert.Equal(1, heavy.BadPageCount);
        Assert.True(heavy.WillFail);
        Assert.False(heavy.CanShrink);
    }

    [Fact]
    public void A_long_comic_is_a_note_not_a_failure()
    {
        var archive = Path.Combine(_root, "long.cbz");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            for (var i = 1; i <= 35; i++)
                Add(zip, $"{i:00}.jpg", TestPictures.Jpeg());
        }

        var heavy = Weigher.Inspect(_group, archive);

        Assert.NotNull(heavy);
        Assert.Equal([Trouble.ManyBatches], heavy.Troubles);
        Assert.Equal(4, heavy.Batches);
        Assert.False(heavy.WillFail);
    }

    [Fact]
    public void A_short_clean_comic_is_not_reported_at_all()
    {
        var archive = Path.Combine(_root, "fine.cbz");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            Add(zip, "01.jpg", TestPictures.Jpeg());
            Add(zip, "02.jpg", TestPictures.Jpeg());
        }

        Assert.Null(Weigher.Inspect(_group, archive));
    }

    [Fact]
    public void A_video_over_the_limit_is_reported_but_not_offered_a_shrink()
    {
        var path = Path.Combine(_root, "clip.mp4");
        using (var file = File.Create(path))
            file.SetLength(MediaRules.FileLimitBytes + 1);

        var heavy = Weigher.Inspect(_group, path);

        Assert.NotNull(heavy);
        Assert.Equal(MediaKind.Video, heavy.Kind);
        Assert.False(heavy.CanShrink);
        Assert.False(Slimmer.Shrink(heavy).Ok);
    }

    [Fact]
    public void The_bot_api_limits_are_the_ones_intake_uses_too()
    {
        Assert.Equal(MediaRules.PhotoLimitBytes, MediaRules.ByteLimit(MediaKind.Photo));
        Assert.Equal(MediaRules.FileLimitBytes, MediaRules.ByteLimit(MediaKind.Video));
        Assert.Null(MediaRules.ByteLimit(MediaKind.Comic));
        Assert.True(MediaRules.IsOverByteLimit("a.jpg", MediaRules.PhotoLimitBytes + 1));
        Assert.False(MediaRules.IsOverByteLimit("a.cbz", long.MaxValue));
        Assert.True(MediaRules.IsPhotoTooLarge(6000, 4200));
        Assert.True(MediaRules.IsPhotoTooLarge(2100, 100));
        Assert.False(MediaRules.IsPhotoTooLarge(4000, 3000));
    }

    private static void Add(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        stream.Write(bytes);
    }
}
