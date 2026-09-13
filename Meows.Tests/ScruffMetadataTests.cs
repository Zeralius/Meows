using System.Buffers.Binary;
using System.Text;
using Meows.Media;
using Meows.Plugins.Scruff.Services;
using SkiaSharp;

namespace Meows.Tests;

/// <summary>
/// Pictures built here rather than checked in, so each test says exactly what it planted and
/// then looks for it. The pixels come from Skia, the metadata is written by hand, because the
/// whole point is knowing what was in the file before Scruff touched it.
/// </summary>
public static class TestPictures
{
    public static byte[] Jpeg(int width = 40, int height = 24, SKColor? colour = null)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(colour ?? SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    public static byte[] Png(int width = 40, int height = 24, bool transparent = false)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        bitmap.Erase(transparent ? new SKColor(255, 0, 0, 128) : SKColors.DarkOrange);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static byte[] WebP(int width = 40, int height = 24)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.SeaGreen);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Webp, 90);
        return data.ToArray();
    }

    /// <summary>
    /// A picture that is not flat, so a turn can be told from a mirror: the top left corner is
    /// red, the rest is blue.
    /// </summary>
    public static byte[] MarkedJpeg(int width = 40, int height = 24)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.Blue);
        using var canvas = new SKCanvas(bitmap);
        canvas.DrawRect(0, 0, 8, 8, new SKPaint { Color = SKColors.Red });
        canvas.Flush();
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 100);
        return data.ToArray();
    }

    /// <summary>
    /// The same JPEG with an APP1 EXIF segment slipped in after the SOI, carrying an orientation
    /// and, if asked, a GPS directory with a latitude in it.
    /// </summary>
    public static byte[] WithExif(byte[] jpeg, int orientation, bool gps = false, bool emptyGps = false)
    {
        var tiff = Tiff(orientation, gps || emptyGps, emptyGps);
        var payload = Concat("Exif\0\0"u8.ToArray(), tiff);
        return InsertSegment(jpeg, 0xE1, payload);
    }

    public static byte[] WithComment(byte[] jpeg, string comment) =>
        InsertSegment(jpeg, 0xFE, Encoding.ASCII.GetBytes(comment));

    public static byte[] WithXmp(byte[] jpeg) =>
        InsertSegment(jpeg, 0xE1, Concat("http://ns.adobe.com/xap/1.0/\0"u8.ToArray(), "<x:xmpmeta/>"u8.ToArray()));

    private static byte[] InsertSegment(byte[] jpeg, byte marker, byte[] payload)
    {
        var length = payload.Length + 2;
        var segment = new byte[4 + payload.Length];
        segment[0] = 0xFF;
        segment[1] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)length);
        payload.CopyTo(segment, 4);
        return Concat(jpeg[..2], segment, jpeg[2..]);
    }

    /// <summary>Little endian TIFF: IFD0 with orientation and optionally a GPS pointer, then the GPS IFD.</summary>
    public static byte[] Tiff(int orientation, bool gps, bool emptyGps = false)
    {
        var entries = gps ? 2 : 1;
        var ifd0Size = 2 + entries * 12 + 4;
        var gpsOffset = 8 + ifd0Size;

        var stream = new MemoryStream();
        var w = new BinaryWriter(stream);
        w.Write((byte)'I'); w.Write((byte)'I'); w.Write((ushort)42); w.Write(8u);

        w.Write((ushort)entries);
        w.Write((ushort)0x0112); w.Write((ushort)3); w.Write(1u); w.Write((ushort)orientation); w.Write((ushort)0);
        if (gps)
        {
            w.Write((ushort)0x8825); w.Write((ushort)4); w.Write(1u); w.Write((uint)gpsOffset);
        }
        w.Write(0u);

        if (gps)
        {
            // Version, then a latitude reference, which is the kind of entry that says somewhere.
            // An empty directory has the version and nothing else.
            w.Write((ushort)(emptyGps ? 1 : 2));
            w.Write((ushort)0x0000); w.Write((ushort)1); w.Write(4u); w.Write((byte)2); w.Write((byte)3); w.Write((byte)0); w.Write((byte)0);
            if (!emptyGps)
            {
                w.Write((ushort)0x0001); w.Write((ushort)2); w.Write(2u); w.Write((byte)'N'); w.Write((byte)0); w.Write((ushort)0);
            }
            w.Write(0u);
        }

        return stream.ToArray();
    }

    public static byte[] PngWithChunks(byte[] png, params (string Type, byte[] Data)[] chunks)
    {
        // After IHDR, which is always the first chunk and 25 bytes with its wrapper.
        var head = png[..33];
        var rest = png[33..];
        var extra = chunks.Select(c => Chunk(c.Type, c.Data)).ToArray();
        return Concat([head, .. extra, rest]);
    }

    public static byte[] Chunk(string type, byte[] data)
    {
        var chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), Crc(chunk.AsSpan(4, 4 + data.Length)));
        return chunk;
    }

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    /// <summary>A simple WebP rewrapped as an extended one with an EXIF chunk beside the picture.</summary>
    public static byte[] WebPWithExif(byte[] simple, int width, int height)
    {
        var picture = simple[12..];
        var tiff = Tiff(1, gps: true);

        var vp8x = new byte[10];
        vp8x[0] = 0x08; // EXIF present
        Write24(vp8x.AsSpan(4), width - 1);
        Write24(vp8x.AsSpan(7), height - 1);

        var body = Concat(RiffChunk("VP8X", vp8x), picture, RiffChunk("EXIF", tiff));
        var file = new byte[12 + body.Length];
        "RIFF"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)(4 + body.Length));
        "WEBP"u8.CopyTo(file.AsSpan(8));
        body.CopyTo(file, 12);
        return file;
    }

    private static void Write24(Span<byte> span, int value)
    {
        span[0] = (byte)value;
        span[1] = (byte)(value >> 8);
        span[2] = (byte)(value >> 16);
    }

    private static byte[] RiffChunk(string type, byte[] data)
    {
        var padded = data.Length + (data.Length & 1);
        var chunk = new byte[8 + padded];
        Encoding.ASCII.GetBytes(type).CopyTo(chunk, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), (uint)data.Length);
        data.CopyTo(chunk, 8);
        return chunk;
    }

    public static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var at = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, at);
            at += part.Length;
        }
        return result;
    }

    /// <summary>Everything from the start of scan marker onward, which is the compressed picture itself.</summary>
    public static byte[] ScanOf(byte[] jpeg)
    {
        for (var i = 2; i + 1 < jpeg.Length; i++)
        {
            if (jpeg[i] == 0xFF && jpeg[i + 1] == 0xDA)
                return jpeg[i..];
        }
        return [];
    }
}

public class ScruffMetadataTests
{
    [Fact]
    public void A_plain_jpeg_carries_nothing_and_says_its_size()
    {
        var report = Metadata.Inspect(TestPictures.Jpeg(40, 24));

        Assert.Equal(ImageFormat.Jpeg, report.Format);
        Assert.Equal(40, report.Width);
        Assert.Equal(24, report.Height);
        Assert.False(report.CarriesAnything);
        Assert.Equal(1, report.Orientation);
    }

    [Fact]
    public void Exif_with_gps_is_seen_for_what_it_is()
    {
        var file = TestPictures.WithExif(TestPictures.Jpeg(), orientation: 6, gps: true);

        var report = Metadata.Inspect(file);

        Assert.True(report.HasExif);
        Assert.True(report.HasGps);
        Assert.Equal(6, report.Orientation);
        Assert.True(report.NeedsTurning);
        Assert.Equal(["GPS", "EXIF"], report.Carried);
    }

    [Fact]
    public void A_gps_directory_with_only_a_version_in_it_is_not_a_location()
    {
        // Some cameras write the block and put nothing in it. Waving a GPS badge over that would
        // teach people to ignore the badge.
        var file = TestPictures.WithExif(TestPictures.Jpeg(), orientation: 1, emptyGps: true);

        var report = Metadata.Inspect(file);

        Assert.True(report.HasExif);
        Assert.False(report.HasGps);
    }

    [Fact]
    public void Stripping_a_jpeg_removes_the_metadata_and_leaves_the_picture_byte_for_byte()
    {
        var plain = TestPictures.Jpeg();
        var loaded = TestPictures.WithXmp(TestPictures.WithComment(TestPictures.WithExif(plain, 1, gps: true), "shot on my phone"));

        var before = Metadata.Inspect(loaded);
        Assert.True(before.HasExif && before.HasXmp && before.HasComments);

        var stripped = Metadata.Strip(loaded);
        var after = Metadata.Inspect(stripped);

        Assert.False(after.CarriesAnything);
        Assert.Equal(TestPictures.ScanOf(plain), TestPictures.ScanOf(stripped));
        Assert.True(stripped.Length < loaded.Length);

        // Still a picture Skia will open, with the same size.
        using var bitmap = SKBitmap.Decode(stripped);
        Assert.NotNull(bitmap);
        Assert.Equal(40, bitmap.Width);
    }

    [Fact]
    public void Png_text_and_exif_chunks_go_and_the_picture_stays()
    {
        var plain = TestPictures.Png();
        var loaded = TestPictures.PngWithChunks(plain,
            ("tEXt", "Author\0Somebody"u8.ToArray()),
            ("eXIf", TestPictures.Tiff(1, gps: true)),
            ("iTXt", "XML:com.adobe.xmp\0\0\0\0\0<x:xmpmeta/>"u8.ToArray()));

        var before = Metadata.Inspect(loaded);
        Assert.True(before.HasComments);
        Assert.True(before.HasExif);
        Assert.True(before.HasGps);
        Assert.True(before.HasXmp);
        Assert.Equal(40, before.Width);

        var stripped = Metadata.Strip(loaded);

        Assert.False(Metadata.Inspect(stripped).CarriesAnything);
        Assert.Equal(plain, stripped);
    }

    [Fact]
    public void Webp_exif_goes_and_the_flag_goes_with_it()
    {
        var plain = TestPictures.WebP(40, 24);
        var loaded = TestPictures.WebPWithExif(plain, 40, 24);

        var before = Metadata.Inspect(loaded);
        Assert.Equal(ImageFormat.WebP, before.Format);
        Assert.True(before.HasExif);
        Assert.True(before.HasGps);
        Assert.Equal(40, before.Width);
        Assert.Equal(24, before.Height);

        var stripped = Metadata.Strip(loaded);
        var after = Metadata.Inspect(stripped);

        Assert.False(after.CarriesAnything);
        Assert.Equal((byte)0, stripped[20] & 0x0C);

        // The RIFF size was rewritten to match what was kept.
        Assert.Equal((uint)(stripped.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(stripped.AsSpan(4)));

        using var bitmap = SKBitmap.Decode(stripped);
        Assert.NotNull(bitmap);
        Assert.Equal(40, bitmap.Width);
    }

    [Fact]
    public void Something_that_is_not_a_picture_comes_back_untouched()
    {
        var bytes = "definitely a spreadsheet"u8.ToArray();

        Assert.Equal(ImageFormat.Unknown, Metadata.Inspect(bytes).Format);
        Assert.Same(bytes, Metadata.Strip(bytes));
    }

    [Fact]
    public void The_icc_profile_is_the_one_thing_kept()
    {
        var jpeg = TestPictures.Jpeg();
        var icc = TestPictures.Concat("ICC_PROFILE\0"u8.ToArray(), [1, 1], new byte[32]);
        var segment = new byte[4 + icc.Length];
        segment[0] = 0xFF;
        segment[1] = 0xE2;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(icc.Length + 2));
        icc.CopyTo(segment, 4);
        var loaded = TestPictures.Concat(jpeg[..2], segment, jpeg[2..]);

        var stripped = Metadata.Strip(loaded);

        Assert.True(Metadata.Inspect(stripped).HasIcc);
        Assert.Equal(loaded.Length, stripped.Length);
    }
}
