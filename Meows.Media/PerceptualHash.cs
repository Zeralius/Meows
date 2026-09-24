using System.Numerics;
using SkiaSharp;

namespace Meows.Media;

/// <summary>A picture's look in 64 bits, and its size in pixels, for telling near copies apart from strangers.</summary>
public readonly record struct Look(ulong Hash, int Width, int Height)
{
    public long Pixels => (long)Width * Height;
}

/// <summary>
/// A perceptual hash: the picture as 32 by 32 cells of grey, turned into frequencies with a
/// discrete cosine transform, and one bit for each of the 64 lowest frequencies saying whether
/// it is above their median. Resizing, re-encoding, a format change and a light watermark barely
/// move it; a different picture moves about half of it. Low frequencies are the shape of the
/// picture rather than its noise, which is what keeps flat colour, drawn art's usual background,
/// from flipping bits on encoder noise the way comparing neighbouring pixels does.
///
/// A match on this is an opinion, never a fact. Two pages of the same comic can score as close
/// as two copies of one page, which is why whatever uses it keeps its threshold tight and shows
/// both pictures before anything is thrown away.
/// </summary>
public static class PerceptualHash
{
    /// <summary>The grid the picture is averaged into before the transform.</summary>
    private const int Size = 32;

    /// <summary>The corner of the transform kept: 8 by 8 low frequencies, 64 bits.</summary>
    private const int Kept = 8;

    private static readonly double[,] Cosines = BuildCosines();

    private static double[,] BuildCosines()
    {
        var table = new double[Size, Size];
        for (var u = 0; u < Size; u++)
        for (var x = 0; x < Size; x++)
            table[u, x] = Math.Cos((2 * x + 1) * u * Math.PI / (2 * Size));
        return table;
    }

    /// <summary>The look of a picture file, or null when it cannot be decoded.</summary>
    public static Look? Of(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Of(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static Look? Of(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return Of(stream);
    }

    /// <summary>
    /// Decoded at a fraction of its size where the format allows, since nine by eight is all that
    /// is wanted from it and a 40 megapixel scan decoded in full for that would be most of the
    /// scan's time.
    /// </summary>
    public static Look? Of(Stream stream)
    {
        try
        {
            using var codec = SKCodec.Create(stream);
            if (codec is null)
                return null;

            var full = codec.Info;
            var scaled = codec.GetScaledDimensions(Math.Max(1f / 8, Math.Min(1f, 64f / Math.Max(1, Math.Min(full.Width, full.Height)))));
            var info = new SKImageInfo(scaled.Width, scaled.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var decoded = new SKBitmap(info);
            var result = codec.GetPixels(info, decoded.GetPixels());
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                return null;

            var grey = Cells(decoded);
            var frequencies = Transform(grey);

            // The 64 lowest frequencies, and their median leaving out the first, which is only the
            // picture's overall brightness and would drag the median with it.
            var low = new double[Kept * Kept];
            for (var v = 0; v < Kept; v++)
            for (var u = 0; u < Kept; u++)
                low[v * Kept + u] = frequencies[v * Size + u];
            var median = low.Skip(1).Order().ElementAt((low.Length - 1) / 2);

            ulong hash = 0;
            for (var i = 0; i < low.Length; i++)
            {
                if (low[i] > median)
                    hash |= 1UL << i;
            }

            return new Look(hash, full.Width, full.Height);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The picture as 32 by 32 cells of grey, each the average of every pixel that falls in it.
    /// Averaging rather than sampling one pixel per cell keeps a re-encoded or resized copy close:
    /// a sampled pixel lands on whatever the encoder's noise put there.
    /// </summary>
    private static double[] Cells(SKBitmap bitmap)
    {
        const int Columns = Size, Rows = Size;
        var sums = new double[Columns * Rows];
        var counts = new int[Columns * Rows];
        var width = bitmap.Width;
        var height = bitmap.Height;
        var pixels = bitmap.Pixels;

        for (var y = 0; y < height; y++)
        {
            var row = Math.Min(Rows - 1, y * Rows / height);
            for (var x = 0; x < width; x++)
            {
                var column = Math.Min(Columns - 1, x * Columns / width);
                var c = pixels[y * width + x];
                var cell = row * Columns + column;
                sums[cell] += 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue;
                counts[cell]++;
            }
        }

        for (var i = 0; i < sums.Length; i++)
            sums[i] = counts[i] == 0 ? 0 : sums[i] / counts[i];
        return sums;
    }

    /// <summary>The two-dimensional cosine transform of the grid, done a row and then a column at a time.</summary>
    private static double[] Transform(double[] grey)
    {
        var rows = new double[Size * Size];
        for (var y = 0; y < Size; y++)
        for (var u = 0; u < Size; u++)
        {
            double sum = 0;
            for (var x = 0; x < Size; x++)
                sum += grey[y * Size + x] * Cosines[u, x];
            rows[y * Size + u] = sum;
        }

        var result = new double[Size * Size];
        for (var u = 0; u < Size; u++)
        for (var v = 0; v < Size; v++)
        {
            double sum = 0;
            for (var y = 0; y < Size; y++)
                sum += rows[y * Size + u] * Cosines[v, y];
            result[v * Size + u] = sum;
        }
        return result;
    }

    /// <summary>How many of the 64 bits differ. Zero is the same look; above ten is usually another picture.</summary>
    public static int Distance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);
}
