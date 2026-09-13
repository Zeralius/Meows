using System.Buffers.Binary;
using System.Text;

namespace Meows.Plugins.Scruff.Services;

public enum ImageFormat
{
    Unknown,
    Jpeg,
    Png,
    WebP,
    Gif,
}

/// <summary>
/// What a file carries besides its pixels.
///
/// Everything in here is read without decoding the picture, because the question is not what
/// the picture looks like but what else is riding along with it: the camera, the lens, the
/// time, and on a phone photo the place it was taken.
/// </summary>
public sealed record MetadataReport
{
    public ImageFormat Format { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public bool HasExif { get; init; }

    /// <summary>The one that matters most. Coordinates, in a file that is about to be public.</summary>
    public bool HasGps { get; init; }

    public bool HasXmp { get; init; }

    /// <summary>IPTC or Photoshop blocks, which carry captions, keywords and sometimes a name.</summary>
    public bool HasIptc { get; init; }

    /// <summary>A colour profile. Kept, because stripping it changes how the picture looks.</summary>
    public bool HasIcc { get; init; }

    /// <summary>Free text: JPEG comments, PNG text chunks, timestamps.</summary>
    public bool HasComments { get; init; }

    /// <summary>
    /// EXIF orientation, 1 to 8, where 1 is the way the pixels already are. Anything else means
    /// the picture only looks right because a viewer read the tag, and taking the tag away
    /// without turning the pixels would leave it on its side.
    /// </summary>
    public int Orientation { get; init; } = 1;

    public bool NeedsTurning => Orientation is >= 2 and <= 8;

    /// <summary>Whether there is anything here worth removing.</summary>
    public bool CarriesAnything => HasExif || HasGps || HasXmp || HasIptc || HasComments;

    public bool HasSize => Width > 0 && Height > 0;

    /// <summary>The kinds found, as short names for a badge or a log line.</summary>
    public IReadOnlyList<string> Carried
    {
        get
        {
            var found = new List<string>();
            if (HasGps) found.Add("GPS");
            if (HasExif) found.Add("EXIF");
            if (HasXmp) found.Add("XMP");
            if (HasIptc) found.Add("IPTC");
            if (HasComments) found.Add("text");
            return found;
        }
    }
}

/// <summary>
/// Reads and removes metadata by walking the container, never by decoding the picture.
///
/// That is the point of doing it this way rather than loading and saving: a JPEG that is
/// stripped here has exactly the pixels it had before, byte for byte, because the compressed
/// scan is copied through untouched. Only the segments around it change. The same holds for
/// PNG chunks and WebP chunks. Re-encoding is reserved for the one case that needs it, which is
/// a picture whose orientation lives in the tag being removed.
/// </summary>
public static class Metadata
{
    private static readonly byte[] JpegStart = [0xFF, 0xD8];
    private static readonly byte[] PngStart = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] ExifHeader = "Exif\0\0"u8.ToArray();
    private static readonly byte[] XmpHeader = "http://ns.adobe.com/xap/1.0/\0"u8.ToArray();
    private static readonly byte[] XmpExtendedHeader = "http://ns.adobe.com/xmp/extension/\0"u8.ToArray();
    private static readonly byte[] IccHeader = "ICC_PROFILE\0"u8.ToArray();
    private static readonly byte[] PhotoshopHeader = "Photoshop 3.0\0"u8.ToArray();
    private static readonly byte[] AdobeHeader = "Adobe"u8.ToArray();
    private static readonly byte[] JfifHeader = "JFIF\0"u8.ToArray();

    public static ImageFormat Sniff(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return ImageFormat.Jpeg;
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(PngStart))
            return ImageFormat.Png;
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
            return ImageFormat.WebP;
        if (bytes.Length >= 6 && (bytes[..6].SequenceEqual("GIF89a"u8) || bytes[..6].SequenceEqual("GIF87a"u8)))
            return ImageFormat.Gif;
        return ImageFormat.Unknown;
    }

    public static string Extension(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => ".jpg",
        ImageFormat.Png => ".png",
        ImageFormat.WebP => ".webp",
        ImageFormat.Gif => ".gif",
        _ => ".bin",
    };

    public static string MimeType(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => "image/jpeg",
        ImageFormat.Png => "image/png",
        ImageFormat.WebP => "image/webp",
        ImageFormat.Gif => "image/gif",
        _ => "application/octet-stream",
    };

    public static MetadataReport Inspect(byte[] bytes) => Sniff(bytes) switch
    {
        ImageFormat.Jpeg => WalkJpeg(bytes, null),
        ImageFormat.Png => WalkPng(bytes, null),
        ImageFormat.WebP => WalkWebP(bytes, null),
        var other => new MetadataReport { Format = other },
    };

    /// <summary>
    /// The same bytes with the metadata taken out. The pixels are untouched.
    ///
    /// A format this does not know how to walk comes back exactly as it went in, so the caller
    /// can always write the result without checking. GIF is one of those on purpose: what it
    /// can carry is a comment block almost nothing writes, and re-walking an animation to find
    /// one is not worth the risk of breaking it.
    /// </summary>
    public static byte[] Strip(byte[] bytes)
    {
        switch (Sniff(bytes))
        {
            case ImageFormat.Jpeg:
            {
                var output = new MemoryStream(bytes.Length);
                WalkJpeg(bytes, output);
                return output.ToArray();
            }
            case ImageFormat.Png:
            {
                var output = new MemoryStream(bytes.Length);
                WalkPng(bytes, output);
                return output.ToArray();
            }
            case ImageFormat.WebP:
            {
                var output = new MemoryStream(bytes.Length);
                WalkWebP(bytes, output);
                return output.ToArray();
            }
            default:
                return bytes;
        }
    }

    // ---- JPEG ---------------------------------------------------------------------------------

    /// <summary>
    /// Segment by segment up to the start of scan, then everything after it verbatim.
    ///
    /// The kept list is short and deliberate: JFIF, the ICC profile, and Adobe's APP14, without
    /// which some CMYK and YCCK files decode with their colours inverted. Everything else in the
    /// APP range is either metadata or a vendor block nobody needs to render the picture.
    /// </summary>
    private static MetadataReport WalkJpeg(byte[] bytes, Stream? output)
    {
        var report = new MetadataReport { Format = ImageFormat.Jpeg };
        output?.Write(JpegStart);

        var position = 2;
        while (position + 4 <= bytes.Length)
        {
            if (bytes[position] != 0xFF)
            {
                // Not on a marker. The file is odd; copy the rest through rather than guess.
                output?.Write(bytes, position, bytes.Length - position);
                return report;
            }

            var marker = bytes[position + 1];

            // Padding between segments, permitted by the standard.
            if (marker == 0xFF)
            {
                position++;
                continue;
            }

            // Start of scan: the compressed picture follows and runs to the end. Nothing in
            // there is metadata, and nothing in there is safe to parse casually.
            if (marker == 0xDA)
            {
                output?.Write(bytes, position, bytes.Length - position);
                return report;
            }

            // Standalone markers carry no length.
            if (marker == 0xD8 || marker == 0x01 || marker is >= 0xD0 and <= 0xD7)
            {
                output?.Write(bytes, position, 2);
                position += 2;
                continue;
            }

            if (marker == 0xD9)
            {
                output?.Write(bytes, position, 2);
                return report;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(position + 2));
            if (length < 2 || position + 2 + length > bytes.Length)
            {
                output?.Write(bytes, position, bytes.Length - position);
                return report;
            }

            var payload = bytes.AsSpan(position + 4, length - 2);
            var keep = true;

            switch (marker)
            {
                case 0xE0:
                    keep = payload.StartsWith(JfifHeader);
                    break;

                case 0xE1:
                    keep = false;
                    if (payload.StartsWith(ExifHeader))
                        report = ReadTiff(payload[ExifHeader.Length..], report) with { HasExif = true };
                    else if (payload.StartsWith(XmpHeader) || payload.StartsWith(XmpExtendedHeader))
                        report = report with { HasXmp = true };
                    break;

                case 0xE2:
                    keep = payload.StartsWith(IccHeader);
                    if (keep)
                        report = report with { HasIcc = true };
                    break;

                case 0xED:
                    keep = false;
                    if (payload.StartsWith(PhotoshopHeader))
                        report = report with { HasIptc = true };
                    break;

                case 0xEE:
                    keep = payload.StartsWith(AdobeHeader);
                    break;

                case 0xFE:
                    keep = false;
                    report = report with { HasComments = true };
                    break;

                case >= 0xE3 and <= 0xEF:
                    // Vendor blocks: Meta, Ducky, picture info. Not needed to draw the picture,
                    // and not worth reading to find out what each one says about the author.
                    keep = false;
                    break;

                case 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7
                    or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF:
                    if (payload.Length >= 5)
                    {
                        report = report with
                        {
                            Height = BinaryPrimitives.ReadUInt16BigEndian(payload[1..]),
                            Width = BinaryPrimitives.ReadUInt16BigEndian(payload[3..]),
                        };
                    }
                    break;
            }

            if (keep)
                output?.Write(bytes, position, 2 + length);

            position += 2 + length;
        }

        return report;
    }

    // ---- PNG ----------------------------------------------------------------------------------

    private static readonly HashSet<string> PngDropped =
        ["tEXt", "zTXt", "iTXt", "eXIf", "tIME"];

    private static MetadataReport WalkPng(byte[] bytes, Stream? output)
    {
        var report = new MetadataReport { Format = ImageFormat.Png };
        output?.Write(PngStart);

        var position = 8;
        while (position + 8 <= bytes.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position));
            var type = Encoding.ASCII.GetString(bytes, position + 4, 4);
            var total = 12 + length;

            if (length < 0 || position + total > bytes.Length)
            {
                output?.Write(bytes, position, bytes.Length - position);
                return report;
            }

            var data = bytes.AsSpan(position + 8, length);

            switch (type)
            {
                case "IHDR" when length >= 8:
                    report = report with
                    {
                        Width = (int)BinaryPrimitives.ReadUInt32BigEndian(data),
                        Height = (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]),
                    };
                    break;

                case "eXIf":
                    report = ReadTiff(data, report) with { HasExif = true };
                    break;

                case "iTXt":
                    // XMP travels in an iTXt chunk with a fixed keyword. Anything else is text.
                    if (data.StartsWith("XML:com.adobe.xmp\0"u8))
                        report = report with { HasXmp = true };
                    else
                        report = report with { HasComments = true };
                    break;

                case "tEXt" or "zTXt" or "tIME":
                    report = report with { HasComments = true };
                    break;

                case "iCCP":
                    report = report with { HasIcc = true };
                    break;
            }

            if (!PngDropped.Contains(type))
                output?.Write(bytes, position, total);

            position += total;

            if (type == "IEND")
                break;
        }

        return report;
    }

    // ---- WebP ---------------------------------------------------------------------------------

    /// <summary>
    /// RIFF chunks. An extended file (VP8X) announces EXIF and XMP in a flags byte as well as
    /// carrying them as chunks, so both the chunk and the flag go, and the RIFF size is written
    /// again at the end to match what was actually kept.
    /// </summary>
    private static MetadataReport WalkWebP(byte[] bytes, Stream? output)
    {
        var report = new MetadataReport { Format = ImageFormat.WebP };

        // RIFF header, size patched afterwards.
        output?.Write(bytes, 0, 12);

        var position = 12;
        while (position + 8 <= bytes.Length)
        {
            var type = Encoding.ASCII.GetString(bytes, position, 4);
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 4));
            var padded = length + (length & 1);
            var total = 8 + padded;

            if (length < 0 || position + 8 + length > bytes.Length)
            {
                output?.Write(bytes, position, bytes.Length - position);
                break;
            }

            var data = bytes.AsSpan(position + 8, length);
            var keep = true;

            switch (type)
            {
                case "VP8X" when length >= 10:
                    report = report with
                    {
                        Width = 1 + (data[4] | data[5] << 8 | data[6] << 16),
                        Height = 1 + (data[7] | data[8] << 8 | data[9] << 16),
                        HasIcc = (data[0] & 0x20) != 0,
                    };

                    if (output is not null)
                    {
                        // Same chunk, with the EXIF (0x08) and XMP (0x04) bits cleared.
                        output.Write(bytes, position, 8);
                        output.WriteByte((byte)(data[0] & ~0x0C));
                        output.Write(bytes, position + 9, padded - 1);
                        keep = false;
                    }
                    break;

                case "VP8 " when length >= 10 && !report.HasSize:
                    report = report with
                    {
                        Width = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]) & 0x3FFF,
                        Height = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]) & 0x3FFF,
                    };
                    break;

                case "VP8L" when length >= 5 && !report.HasSize:
                {
                    var bits = BinaryPrimitives.ReadUInt32LittleEndian(data[1..]);
                    report = report with
                    {
                        Width = (int)(bits & 0x3FFF) + 1,
                        Height = (int)((bits >> 14) & 0x3FFF) + 1,
                    };
                    break;
                }

                case "EXIF":
                    report = ReadTiff(data, report) with { HasExif = true };
                    keep = false;
                    break;

                case "XMP ":
                    report = report with { HasXmp = true };
                    keep = false;
                    break;
            }

            if (keep)
                output?.Write(bytes, position, Math.Min(total, bytes.Length - position));

            position += total;
        }

        if (output is { CanSeek: true })
        {
            var size = (uint)(output.Length - 8);
            output.Position = 4;
            Span<byte> four = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(four, size);
            output.Write(four);
            output.Position = output.Length;
        }

        return report;
    }

    // ---- TIFF, which is what EXIF is inside ----------------------------------------------------

    private const int OrientationTag = 0x0112;
    private const int GpsPointerTag = 0x8825;

    /// <summary>
    /// The two things worth knowing from the first directory: which way up the picture is, and
    /// whether there is a GPS directory at all. Nothing else is read, because nothing else
    /// changes what happens next.
    /// </summary>
    private static MetadataReport ReadTiff(ReadOnlySpan<byte> tiff, MetadataReport report)
    {
        if (tiff.Length < 8)
            return report;

        bool little;
        if (tiff[0] == 'I' && tiff[1] == 'I')
            little = true;
        else if (tiff[0] == 'M' && tiff[1] == 'M')
            little = false;
        else
            return report;

        var offset = (int)Read32(tiff, 4, little);
        if (offset < 8 || offset + 2 > tiff.Length)
            return report;

        var count = Read16(tiff, offset, little);
        var entry = offset + 2;

        for (var i = 0; i < count && entry + 12 <= tiff.Length; i++, entry += 12)
        {
            var tag = Read16(tiff, entry, little);
            var type = Read16(tiff, entry + 2, little);

            switch (tag)
            {
                case OrientationTag when type == 3:
                {
                    var value = Read16(tiff, entry + 8, little);
                    if (value is >= 1 and <= 8)
                        report = report with { Orientation = value };
                    break;
                }

                case GpsPointerTag:
                {
                    // The pointer alone is not proof: some cameras write an empty GPS
                    // directory with only the version tag in it. Look for anything beyond that.
                    var gps = (int)Read32(tiff, entry + 8, little);
                    report = report with { HasGps = GpsDirectorySaysAnything(tiff, gps, little) };
                    break;
                }
            }
        }

        return report;
    }

    private static bool GpsDirectorySaysAnything(ReadOnlySpan<byte> tiff, int offset, bool little)
    {
        if (offset < 8 || offset + 2 > tiff.Length)
            return false;

        var count = Read16(tiff, offset, little);
        var entry = offset + 2;
        for (var i = 0; i < count && entry + 12 <= tiff.Length; i++, entry += 12)
        {
            // 0x0000 is GPSVersionID, which says nothing about where anyone was.
            if (Read16(tiff, entry, little) != 0)
                return true;
        }

        return false;
    }

    private static ushort Read16(ReadOnlySpan<byte> span, int at, bool little) =>
        little
            ? BinaryPrimitives.ReadUInt16LittleEndian(span[at..])
            : BinaryPrimitives.ReadUInt16BigEndian(span[at..]);

    private static uint Read32(ReadOnlySpan<byte> span, int at, bool little) =>
        little
            ? BinaryPrimitives.ReadUInt32LittleEndian(span[at..])
            : BinaryPrimitives.ReadUInt32BigEndian(span[at..]);
}
