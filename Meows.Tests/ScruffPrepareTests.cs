using Meows.Media;
using Meows.Plugins.Scruff.Services;
using SkiaSharp;

namespace Meows.Tests;

/// <summary>
/// Cleaning and fitting. These go through Skia for real, since the thing being checked is what
/// comes out of the encoder, not what was asked of it.
/// </summary>
public class ScruffPrepareTests
{
    private static readonly MediaLimits Bluesky = new BlueskyTarget(new HttpClient()).Limits;

    [Fact]
    public void A_clean_file_is_stripped_and_not_re_encoded()
    {
        var file = TestPictures.WithComment(TestPictures.Jpeg(), "hello");

        var clean = Preparer.Clean(file);

        Assert.False(clean.Reencoded);
        Assert.False(clean.Turned);
        Assert.Equal(ImageFormat.Jpeg, clean.Format);
        Assert.True(clean.Original.HasComments);
        Assert.Equal(TestPictures.ScanOf(file), TestPictures.ScanOf(clean.Bytes));
    }

    [Fact]
    public void A_picture_on_its_side_is_turned_before_the_tag_goes()
    {
        // Orientation 6 means the pixels are stored rotated 90 degrees anticlockwise from how
        // they should be viewed, so viewing means turning them 90 degrees clockwise.
        var file = TestPictures.WithExif(TestPictures.MarkedJpeg(40, 24), orientation: 6);

        var clean = Preparer.Clean(file);

        Assert.True(clean.Turned);
        Assert.True(clean.Reencoded);
        Assert.Equal(24, clean.Width);
        Assert.Equal(40, clean.Height);
        Assert.Equal(1, Metadata.Inspect(clean.Bytes).Orientation);
        Assert.False(Metadata.Inspect(clean.Bytes).HasExif);

        // The red corner was top left; after a clockwise quarter turn it is top right.
        using var bitmap = SKBitmap.Decode(clean.Bytes);
        Assert.True(IsReddish(bitmap.GetPixel(bitmap.Width - 3, 3)), "expected red in the top right");
        Assert.False(IsReddish(bitmap.GetPixel(3, 3)), "expected blue in the top left");
    }

    [Theory]
    [InlineData(2, 40, 24)]
    [InlineData(3, 40, 24)]
    [InlineData(4, 40, 24)]
    [InlineData(5, 24, 40)]
    [InlineData(7, 24, 40)]
    [InlineData(8, 24, 40)]
    public void Every_orientation_produces_the_right_shape(int orientation, int width, int height)
    {
        var clean = Preparer.Clean(TestPictures.WithExif(TestPictures.MarkedJpeg(40, 24), orientation));

        Assert.Equal(width, clean.Width);
        Assert.Equal(height, clean.Height);
    }

    [Fact]
    public void Fitting_shrinks_what_is_too_wide()
    {
        var big = Preparer.Clean(TestPictures.Jpeg(3000, 1500));

        var fitted = Preparer.Fit(big, Bluesky);

        Assert.True(fitted.Shrunk);
        Assert.True(fitted.Fitted);
        Assert.Equal(2000, fitted.Width);
        Assert.Equal(1000, fitted.Height);
        Assert.True(fitted.Bytes.LongLength <= Bluesky.MaxBytes);
    }

    [Fact]
    public void Fitting_gets_under_the_byte_limit()
    {
        // Noise does not compress, which is how a small picture gets to be a big file.
        using var bitmap = new SKBitmap(1200, 1200);
        var random = new Random(7);
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
            bitmap.SetPixel(x, y, new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));

        var clean = Preparer.Clean(Preparer.Encode(bitmap, ImageFormat.Png, 100));
        Assert.True(clean.Bytes.LongLength > 1_000_000);

        var fitted = Preparer.Fit(clean, new MediaLimits(4, 300_000, 0, [ImageFormat.Jpeg, ImageFormat.Png]));

        Assert.True(fitted.Bytes.LongLength <= 300_000, $"still {fitted.Bytes.Length} bytes");
        Assert.True(fitted.Reencoded);
    }

    [Fact]
    public void A_format_a_place_does_not_take_is_converted()
    {
        var webp = Preparer.Clean(TestPictures.WebP());

        var fitted = Preparer.Fit(webp, new FurAffinityTarget().Limits);

        Assert.True(fitted.Converted);
        Assert.Equal(ImageFormat.Jpeg, fitted.Format);
        Assert.Equal(".jpg", fitted.Extension);
    }

    [Fact]
    public void Transparency_survives_a_conversion()
    {
        var png = Preparer.Clean(TestPictures.Png(transparent: true));

        var fitted = Preparer.Fit(png, new MediaLimits(4, 0, 0, [ImageFormat.Jpeg, ImageFormat.WebP]));

        Assert.Equal(ImageFormat.WebP, fitted.Format);
    }

    [Fact]
    public void Nothing_is_done_to_a_file_that_already_fits()
    {
        var clean = Preparer.Clean(TestPictures.Jpeg());

        var fitted = Preparer.Fit(clean, Bluesky);

        Assert.Same(clean.Bytes, fitted.Bytes);
        Assert.False(fitted.Reencoded);
        Assert.True(fitted.Fitted);
    }

    private static bool IsReddish(SKColor colour) => colour.Red > 150 && colour.Blue < 100;
}
