using Meows.Media;
using Meows.Plugins.Purrge.Services;
using Meows.Plugins.Purrge.ViewModels;
using SkiaSharp;

namespace Meows.Tests;

/// <summary>
/// Purrge's look-alikes: the same picture saved differently is found, a different picture is
/// not, identical bytes are left to the duplicate scan, groups never chain, the copy with the
/// most pixels is the one suggested to keep, and nothing can be binned without both on screen.
/// </summary>
public sealed class TabbyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "meows-tabby-" + Guid.NewGuid().ToString("N")[..8]);

    public TabbyTests() => Directory.CreateDirectory(_root);

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

    /// <summary>A picture with a shape to it, the same for the same seed at any size.</summary>
    private static SKBitmap Scene(int seed, int width, int height)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        var random = new Random(seed);
        canvas.Clear(new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
        using var paint = new SKPaint { IsAntialias = true };
        for (var i = 0; i < 12; i++)
        {
            paint.Color = new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            var x = (float)random.NextDouble() * width;
            var y = (float)random.NextDouble() * height;
            var r = (float)(0.1 + random.NextDouble() * 0.3) * Math.Min(width, height);
            canvas.DrawCircle(x, y, r, paint);
        }
        return bitmap;
    }

    private string Save(string name, int seed, int width, int height, ImageFormat format, int quality = 90)
    {
        using var bitmap = Scene(seed, width, height);
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, Preparer.Encode(bitmap, format, quality));
        return path;
    }

    [Fact]
    public void The_same_picture_resized_and_reencoded_looks_alike_and_another_does_not()
    {
        var big = PerceptualHash.Of(Save("big.png", 1, 800, 600, ImageFormat.Png))!.Value;
        var small = PerceptualHash.Of(Save("small.jpg", 1, 400, 300, ImageFormat.Jpeg, 60))!.Value;
        var other = PerceptualHash.Of(Save("other.png", 2, 800, 600, ImageFormat.Png))!.Value;

        Assert.Equal((800, 600), (big.Width, big.Height));
        var near = PerceptualHash.Distance(big.Hash, small.Hash);
        var far = PerceptualHash.Distance(big.Hash, other.Hash);
        Assert.True(near <= LookalikeScanner.DefaultThreshold, $"the same picture was {near} bits apart");
        Assert.True(far > 10, $"a different picture was only {far} bits apart");
        Assert.Null(PerceptualHash.Of("not a picture"u8.ToArray()));
    }

    private static (string, long, DateTime, Look) Fake(string path, ulong hash, int width, long size) =>
        (path, size, DateTime.UtcNow, new Look(hash, width, width));

    [Fact]
    public void Groups_are_stars_around_the_best_copy_and_never_chains()
    {
        // A and B are 3 bits apart, B and C 3, A and C 6: with a threshold of 4, C is not A's.
        var groups = LookalikeScanner.Group(
        [
            Fake("b", 0b111, 400, 100),
            Fake("a", 0, 800, 50),
            Fake("c", 0b111111, 300, 100),
        ], threshold: 4);

        var set = Assert.Single(groups);
        Assert.Equal(["a", "b"], set.Files.Select(f => f.Path));
        Assert.Equal(0, set.Best.Distance);
        Assert.Equal(3, set.Files[1].Distance);
        Assert.Equal(100, set.OthersBytes);
    }

    [Fact]
    public void A_folder_is_scanned_the_best_copy_kept_identical_bytes_left_out_and_access_times_kept()
    {
        var png = Save("cat.png", 7, 1200, 900, ImageFormat.Png);
        var jpg = Save("cat-small.jpg", 7, 600, 450, ImageFormat.Jpeg, 70);
        Save("dog.png", 8, 1200, 900, ImageFormat.Png);
        var twin = Path.Combine(_root, "cat-copy.png");
        File.Copy(png, twin);
        var old = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastAccessTimeUtc(jpg, old);

        var sets = LookalikeScanner.Scan(_root, new ScanOptions(MinimumBytes: 0, SkipSystemFolders: false), LookalikeScanner.DefaultThreshold, null, CancellationToken.None);

        var set = Assert.Single(sets);
        Assert.Equal((1200, 900), (set.Best.Width, set.Best.Height));
        Assert.Contains(set.Files, f => f.Path == jpg);
        // The byte-for-byte copy is the duplicate scan's, and never shows up here beside its twin.
        Assert.False(set.Files.Any(f => f.Path == png) && set.Files.Any(f => f.Path == twin));
        Assert.Equal(old, File.GetLastAccessTimeUtc(jpg));
    }

    [Fact]
    public void Nothing_can_be_binned_without_both_pictures_on_screen_and_the_better_copy_can_win()
    {
        var host = new FakeHost(Path.Combine(_root, "host"));
        using var purrge = new PurrgeViewModel(host);
        purrge.IsLookalikeMode = true;
        Assert.False(purrge.IsDuplicatesMode);
        Assert.False(purrge.IsCompareMode);
        purrge.IsCompareMode = true;
        Assert.False(purrge.IsLookalikeMode);
        purrge.IsLookalikeMode = true;

        var model = purrge.Lookalikes;
        model.Show(
        [
            new LookalikeSet([
                new LookalikeFile(Path.Combine(_root, "a.png"), 900, 1200, 900, DateTime.UtcNow, 0),
                new LookalikeFile(Path.Combine(_root, "b.jpg"), 300, 600, 450, DateTime.UtcNow, 2),
            ]),
        ]);

        Assert.Equal("b.jpg", model.SelectedFile?.FileName);
        Assert.Equal("a.png", model.Keeper?.FileName);
        Assert.False(model.CanRecycle);
        Assert.False(model.RecycleCommand.CanExecute(null));

        model.KeepThisCommand.Execute(null);
        Assert.Equal("b.jpg", model.Keeper?.FileName);
        Assert.Equal("a.png", model.SelectedFile?.FileName);
        Assert.False(model.CanRecycle);
    }
}
