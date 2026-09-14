using SkiaSharp;

namespace Meows.Plugins.Kit.Services;

/// <summary>How the source sits inside the token circle: how far in, and how far off centre.</summary>
/// <param name="Zoom">1 fits the shorter side to the circle; 2 shows half of it, twice as large.</param>
/// <param name="OffsetX">-1 to 1, as a fraction of the circle, positive moving the picture right.</param>
/// <param name="OffsetY">-1 to 1, positive moving the picture down.</param>
public sealed record TokenCrop(float Zoom = 1f, float OffsetX = 0f, float OffsetY = 0f);

/// <summary>
/// The picture work: a token cut round with a ring on it, and a border round a map or a handout.
/// Skia, through the copy the shell already loads, the same way Scruff and Portion draw.
/// </summary>
public static class Pictures
{
    /// <summary>
    /// A token the way the token makers on the web make one: the picture, clipped to the circle
    /// inside the ring, the ring on top, and everything outside the ring transparent. The result
    /// is a square PNG of the frame's size, which is what a VTT wants a token to be.
    /// </summary>
    public static byte[] MakeToken(SKBitmap source, TokenFrame? frame, TokenCrop crop, int size = 512)
    {
        var innerRadius = frame is null ? size / 2f * 0.98f : frame.InnerRadiusFraction * size;
        var centre = size / 2f;

        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        // The picture, scaled so the shorter side spans the circle at zoom 1, then moved.
        var diameter = innerRadius * 2;
        var scale = diameter / Math.Min(source.Width, source.Height) * Math.Max(0.1f, crop.Zoom);
        var drawnW = source.Width * scale;
        var drawnH = source.Height * scale;
        var left = centre - drawnW / 2 + crop.OffsetX * innerRadius;
        var top = centre - drawnH / 2 + crop.OffsetY * innerRadius;

        canvas.Save();
        using (var clip = new SKPath())
        {
            clip.AddCircle(centre, centre, innerRadius);
            canvas.ClipPath(clip, SKClipOperation.Intersect, antialias: true);
        }
        using (var paint = new SKPaint { IsAntialias = true })
        using (var image = SKImage.FromBitmap(source))
        {
            canvas.DrawImage(image, new SKRect(left, top, left + drawnW, top + drawnH),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        }
        canvas.Restore();

        if (frame is not null)
        {
            using var ring = Frames.Bitmap(frame.File);
            using var image = SKImage.FromBitmap(ring);
            using var paint = new SKPaint { IsAntialias = true };
            canvas.DrawImage(image, new SKRect(0, 0, size, size),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        }

        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// A border round a picture, outside it: the canvas grows by the frame's thickness on every
    /// side and the picture sits untouched in the middle, so a map's grid does not move. The
    /// tile's corners are drawn as they are and its edges stretched between them, which is what
    /// a 9-slice is. Returns the padding added, which the manifest records.
    /// </summary>
    public static (byte[] Png, int Padding) AddBorder(SKBitmap source, BorderFrame frame, float scale = 1f)
    {
        var pad = (int)Math.Round(frame.Slice * scale);
        var width = source.Width + 2 * pad;
        var height = source.Height + 2 * pad;

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        using (var image = SKImage.FromBitmap(source))
            canvas.DrawImage(image, pad, pad);

        using var tile = Frames.Bitmap(frame.File);
        using var tileImage = SKImage.FromBitmap(tile);
        var s = frame.Slice;
        var t = frame.Size;
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        using var paint = new SKPaint { IsAntialias = true };

        void Draw(SKRect from, SKRect to) => canvas.DrawImage(tileImage, from, to, sampling, paint);

        // Corners.
        Draw(new SKRect(0, 0, s, s), new SKRect(0, 0, pad, pad));
        Draw(new SKRect(t - s, 0, t, s), new SKRect(width - pad, 0, width, pad));
        Draw(new SKRect(0, t - s, s, t), new SKRect(0, height - pad, pad, height));
        Draw(new SKRect(t - s, t - s, t, t), new SKRect(width - pad, height - pad, width, height));
        // Edges, stretched along their length.
        Draw(new SKRect(s, 0, t - s, s), new SKRect(pad, 0, width - pad, pad));
        Draw(new SKRect(s, t - s, t - s, t), new SKRect(pad, height - pad, width - pad, height));
        Draw(new SKRect(0, s, s, t - s), new SKRect(0, pad, pad, height - pad));
        Draw(new SKRect(t - s, s, t, t - s), new SKRect(width - pad, pad, width, height - pad));

        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        return (data.ToArray(), pad);
    }

    /// <summary>
    /// A first guess at pixels per square, from the three sizes most published maps are drawn
    /// at: the one that divides both sides most nearly into whole squares. A guess to be looked
    /// at, never resized on unseen.
    /// </summary>
    public static double GuessGrid(int width, int height)
    {
        double best = 100;
        var bestError = double.MaxValue;
        foreach (var candidate in new[] { 70.0, 100.0, 140.0, 50.0, 200.0 })
        {
            var w = width / candidate;
            var h = height / candidate;
            var error = Math.Abs(w - Math.Round(w)) + Math.Abs(h - Math.Round(h));
            if (error < bestError - 0.001)
            {
                bestError = error;
                best = candidate;
            }
        }
        return best;
    }

    /// <summary>A map's size in squares, from its pixels and its grid, rounded to the nearest whole square.</summary>
    public static (int Columns, int Rows) Squares(int width, int height, GridSpec grid)
    {
        if (grid.Size <= 0)
            return (0, 0);
        return ((int)Math.Round((width - grid.OffsetX) / grid.Size), (int)Math.Round((height - grid.OffsetY) / grid.Size));
    }
}
