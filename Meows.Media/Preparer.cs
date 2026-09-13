using SkiaSharp;

namespace Meows.Media;

/// <summary>What a place will take. Zero means no limit on that axis.</summary>
public sealed record MediaLimits(int MaxImages, long MaxBytes, int MaxSide, IReadOnlyList<ImageFormat> Accepts)
{
    public bool Takes(ImageFormat format) => Accepts.Contains(format);
}

/// <summary>A file after Scruff has been at it, and what was done to get there.</summary>
public sealed record Prepared
{
    public required byte[] Bytes { get; init; }

    public required ImageFormat Format { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>What the original carried, whether or not any of it was removed.</summary>
    public required MetadataReport Original { get; init; }

    /// <summary>The pixels were turned to match the orientation tag before it was dropped.</summary>
    public bool Turned { get; init; }

    /// <summary>The picture was made smaller.</summary>
    public bool Shrunk { get; init; }

    /// <summary>Written out through an encoder rather than copied, so not byte for byte the same picture.</summary>
    public bool Reencoded { get; init; }

    /// <summary>Written out as a different format from the one it arrived in.</summary>
    public bool Converted { get; init; }

    /// <summary>Not a picture Skia can open, so nothing beyond a lossless strip was possible.</summary>
    public bool Unreadable { get; init; }

    /// <summary>Has been through <see cref="Preparer.Fit"/>, so what it weighs now is what it weighs.</summary>
    public bool Fitted { get; init; }

    public string Extension => Metadata.Extension(Format);

    public string MimeType => Metadata.MimeType(Format);
}

/// <summary>
/// Turns a file into one that is safe to post, and then into one a particular place will take.
///
/// Two steps on purpose. <see cref="Clean"/> is what every file gets, and it is lossless
/// wherever it can be: the metadata is cut out around the compressed picture rather than the
/// picture being decoded and saved again. <see cref="Fit"/> is per destination, because Bluesky
/// will not take a file over a megabyte and FurAffinity will not take WebP, and a file should
/// only be re-encoded for the places that need it, not for all of them because one does.
/// </summary>
public static class Preparer
{
    /// <summary>Quality for a JPEG that has to be written out. High, because this is somebody's art.</summary>
    public const int Quality = 92;

    public static Prepared Clean(byte[] original)
    {
        var report = Metadata.Inspect(original);

        // Nothing to turn, so a lossless strip is the whole job. GIF and anything unknown come
        // back as they went in.
        if (!report.NeedsTurning)
        {
            var stripped = Metadata.Strip(original);
            return new Prepared
            {
                Bytes = stripped,
                Format = report.Format,
                Width = report.Width,
                Height = report.Height,
                Original = report,
            };
        }

        // The orientation lives in the tag being removed, so the pixels have to be turned first.
        using var bitmap = Decode(original);
        if (bitmap is null)
        {
            var stripped = Metadata.Strip(original);
            return new Prepared
            {
                Bytes = stripped,
                Format = report.Format,
                Width = report.Width,
                Height = report.Height,
                Original = report,
                Unreadable = true,
            };
        }

        using var turned = Turn(bitmap, report.Orientation);
        var format = EncodableForm(report.Format, turned);
        return new Prepared
        {
            Bytes = Encode(turned, format, Quality),
            Format = format,
            Width = turned.Width,
            Height = turned.Height,
            Original = report,
            Turned = true,
            Reencoded = true,
            Converted = format != report.Format,
        };
    }

    /// <summary>
    /// Makes a cleaned file acceptable to one place: a format it takes, no longer than its
    /// longest side, and under its byte limit. Comes back unchanged when it already is.
    /// </summary>
    public static Prepared Fit(Prepared clean, MediaLimits limits)
    {
        var tooBig = limits.MaxBytes > 0 && clean.Bytes.LongLength > limits.MaxBytes;
        var tooWide = limits.MaxSide > 0 && Math.Max(clean.Width, clean.Height) > limits.MaxSide;
        var wrongKind = !limits.Takes(clean.Format);

        if (!tooBig && !tooWide && !wrongKind)
            return clean with { Fitted = true };

        // A GIF the place takes is left alone even when it is over the limit. Shrinking one means
        // decoding it, and what comes back is the first frame, which is not the picture that was
        // chosen. Better that the place says no than that a still goes out in an animation's name.
        if (clean.Format == ImageFormat.Gif && !wrongKind)
            return clean with { Fitted = true };

        using var source = Decode(clean.Bytes);
        if (source is null)
            return clean with { Unreadable = true, Fitted = true };

        var format = wrongKind ? Pick(limits, source) : clean.Format;

        var bitmap = source;
        var owned = false;
        var shrunk = false;

        try
        {
            if (tooWide)
            {
                bitmap = Shrink(bitmap, limits.MaxSide);
                owned = true;
                shrunk = true;
            }

            var quality = Quality;
            var bytes = Encode(bitmap, format, quality);

            // Over the byte limit: ease the quality first, since that costs least, and only then
            // make the picture smaller. Each step is modest so the first one that fits is taken.
            while (limits.MaxBytes > 0 && bytes.LongLength > limits.MaxBytes)
            {
                if (format is ImageFormat.Jpeg or ImageFormat.WebP && quality > 60)
                {
                    quality -= 8;
                }
                else if (format == ImageFormat.Png && !HasTransparency(bitmap) && limits.Takes(ImageFormat.Jpeg))
                {
                    // A photograph saved as PNG is the usual reason for this branch.
                    format = ImageFormat.Jpeg;
                    quality = Quality;
                }
                else
                {
                    var smaller = Shrink(bitmap, (int)(Math.Max(bitmap.Width, bitmap.Height) * 0.8));
                    if (owned)
                        bitmap.Dispose();
                    bitmap = smaller;
                    owned = true;
                    shrunk = true;

                    if (Math.Max(bitmap.Width, bitmap.Height) < 200)
                        break;
                }

                bytes = Encode(bitmap, format, quality);
            }

            return clean with
            {
                Bytes = bytes,
                Format = format,
                Width = bitmap.Width,
                Height = bitmap.Height,
                Shrunk = clean.Shrunk || shrunk,
                Reencoded = true,
                Converted = clean.Converted || format != clean.Format,
                Fitted = true,
            };
        }
        finally
        {
            if (owned)
                bitmap.Dispose();
        }
    }

    /// <summary>Whether a decoded picture has any pixel that is not fully opaque.</summary>
    public static bool HasTransparency(SKBitmap bitmap)
    {
        if (bitmap.AlphaType == SKAlphaType.Opaque)
            return false;

        // Sampling the whole thing is fine at these sizes; it is one pass over memory.
        var pixels = bitmap.Pixels;
        foreach (var pixel in pixels)
        {
            if (pixel.Alpha != 255)
                return true;
        }

        return false;
    }

    private static ImageFormat Pick(MediaLimits limits, SKBitmap bitmap)
    {
        // Transparency survives PNG and WebP. A photograph is happier as JPEG.
        if (HasTransparency(bitmap))
        {
            if (limits.Takes(ImageFormat.Png)) return ImageFormat.Png;
            if (limits.Takes(ImageFormat.WebP)) return ImageFormat.WebP;
        }

        if (limits.Takes(ImageFormat.Jpeg)) return ImageFormat.Jpeg;
        if (limits.Takes(ImageFormat.Png)) return ImageFormat.Png;
        if (limits.Takes(ImageFormat.WebP)) return ImageFormat.WebP;
        return ImageFormat.Png;
    }

    /// <summary>The format a turned picture is written back in. GIF cannot be, so it becomes PNG.</summary>
    private static ImageFormat EncodableForm(ImageFormat format, SKBitmap bitmap) => format switch
    {
        ImageFormat.Jpeg or ImageFormat.Png or ImageFormat.WebP => format,
        _ => HasTransparency(bitmap) ? ImageFormat.Png : ImageFormat.Jpeg,
    };

    public static SKBitmap? Decode(byte[] bytes)
    {
        try
        {
            using var codec = SKCodec.Create(new SKMemoryStream(bytes));
            if (codec is null)
                return null;

            var info = codec.Info.WithColorType(SKColorType.Bgra8888).WithAlphaType(SKAlphaType.Unpremul);
            var bitmap = new SKBitmap(info);
            var result = codec.GetPixels(info, bitmap.GetPixels());
            if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput)
                return bitmap;

            bitmap.Dispose();
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Applies an EXIF orientation to the pixels, so the tag can go.
    ///
    /// Each value is a fixed mapping of one corner onto another, written here as the matrix
    /// that does it rather than as a rotate followed by a flip, because that is easier to check
    /// against the table in the standard and harder to get half right.
    /// </summary>
    public static SKBitmap Turn(SKBitmap bitmap, int orientation)
    {
        var w = bitmap.Width;
        var h = bitmap.Height;
        var swaps = orientation is 5 or 6 or 7 or 8;

        var matrix = orientation switch
        {
            2 => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            3 => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            4 => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            5 => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            6 => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
            7 => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
            8 => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
            _ => SKMatrix.Identity,
        };

        var turned = new SKBitmap(swaps ? h : w, swaps ? w : h, bitmap.ColorType, bitmap.AlphaType);
        using var canvas = new SKCanvas(turned);
        canvas.Clear(SKColors.Transparent);
        canvas.SetMatrix(matrix);
        canvas.DrawBitmap(bitmap, 0, 0);
        canvas.Flush();
        return turned;
    }

    public static SKBitmap Shrink(SKBitmap bitmap, int maxSide)
    {
        var scale = maxSide / (double)Math.Max(bitmap.Width, bitmap.Height);
        var width = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
        var height = Math.Max(1, (int)Math.Round(bitmap.Height * scale));

        var info = new SKImageInfo(width, height, bitmap.ColorType, bitmap.AlphaType);
        return bitmap.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell))
               ?? throw new InvalidOperationException("Could not resize the picture.");
    }

    /// <summary>
    /// Writes the pixels out. Skia's encoders write no EXIF, no XMP and no comment, which is
    /// what makes a re-encoded file clean without a second pass.
    /// </summary>
    public static byte[] Encode(SKBitmap bitmap, ImageFormat format, int quality)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = format switch
        {
            ImageFormat.Png => image.Encode(SKEncodedImageFormat.Png, 100),
            ImageFormat.WebP => image.Encode(SKEncodedImageFormat.Webp, quality),
            _ => image.Encode(SKEncodedImageFormat.Jpeg, quality),
        };

        return data?.ToArray() ?? throw new InvalidOperationException("Could not encode the picture.");
    }
}
