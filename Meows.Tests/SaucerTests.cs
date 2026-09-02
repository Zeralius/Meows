using Meows.Plugins.Saucer.Services;
using Meows.Plugins.Saucer.ViewModels;

namespace Meows.Tests;

public sealed class ClipboardConversionTests
{
    /// <summary>
    /// A bare DIB, the shape the clipboard hands over for a bitmap. The palette and the
    /// bitfield masks are part of the data, which is exactly what the conversion has to
    /// account for when working out where the pixels start.
    /// </summary>
    private static byte[] Dib(int width, int height, int bitCount = 32, int compression = 0, int coloursUsed = 0)
    {
        var pixelBytes = width * height * Math.Max(1, bitCount / 8);
        var paletteBytes = bitCount <= 8 ? (coloursUsed != 0 ? coloursUsed : 1 << bitCount) * 4 : 0;
        if (compression == 3)
            paletteBytes += 12;

        var dib = new byte[40 + paletteBytes + pixelBytes];
        BitConverter.GetBytes(40).CopyTo(dib, 0);
        BitConverter.GetBytes(width).CopyTo(dib, 4);
        BitConverter.GetBytes(height).CopyTo(dib, 8);
        BitConverter.GetBytes((short)1).CopyTo(dib, 12);
        BitConverter.GetBytes((short)bitCount).CopyTo(dib, 14);
        BitConverter.GetBytes(compression).CopyTo(dib, 16);
        BitConverter.GetBytes(coloursUsed).CopyTo(dib, 32);
        return dib;
    }

    [Fact]
    public void A_clipboard_bitmap_becomes_a_real_bmp_file()
    {
        var bmp = WindowsClipboard.BmpFromDib(Dib(4, 4));

        Assert.NotNull(bmp);
        Assert.Equal((byte)'B', bmp![0]);
        Assert.Equal((byte)'M', bmp[1]);
        // The header the clipboard leaves off is exactly 14 bytes.
        Assert.Equal(14 + 40 + 4 * 4 * 4, bmp.Length);
        Assert.Equal(bmp.Length, BitConverter.ToInt32(bmp, 2));
    }

    [Fact]
    public void The_pixels_are_pointed_at_correctly_for_a_plain_bitmap()
    {
        var bmp = WindowsClipboard.BmpFromDib(Dib(2, 2));

        // 14 byte file header plus a 40 byte info header, and no palette at 32bpp.
        Assert.Equal(54, BitConverter.ToInt32(bmp!, 10));
    }

    [Fact]
    public void A_palette_is_counted_into_where_the_pixels_start()
    {
        // 8bpp with no explicit count means a full 256 entry palette of four bytes each.
        var bmp = WindowsClipboard.BmpFromDib(Dib(2, 2, bitCount: 8));

        Assert.Equal(14 + 40 + 256 * 4, BitConverter.ToInt32(bmp!, 10));
    }

    [Fact]
    public void Bitfield_masks_are_counted_too()
    {
        // BI_BITFIELDS on a 40 byte header puts three masks before the pixels.
        var bmp = WindowsClipboard.BmpFromDib(Dib(2, 2, compression: 3));

        Assert.Equal(14 + 40 + 12, BitConverter.ToInt32(bmp!, 10));
    }

    [Fact]
    public void Nonsense_is_refused_rather_than_turned_into_a_broken_file()
    {
        Assert.Null(WindowsClipboard.BmpFromDib(null));
        Assert.Null(WindowsClipboard.BmpFromDib([1, 2, 3]));
        Assert.Null(WindowsClipboard.BmpFromDib(new byte[60]));
    }

    [Fact]
    public void A_summary_is_one_readable_line()
    {
        var messy = "first line\r\nsecond line\tand a tab";

        var summary = WindowsClipboard.Summarise(messy);

        Assert.DoesNotContain('\n', summary);
        Assert.DoesNotContain('\r', summary);
        Assert.DoesNotContain('\t', summary);
        Assert.StartsWith("first line", summary);
    }

    [Fact]
    public void A_long_summary_is_cut_short()
    {
        var summary = WindowsClipboard.Summarise(new string('x', 500), limit: 40);

        Assert.True(summary.Length <= 41, $"was {summary.Length}");
        Assert.EndsWith("…", summary);
    }

    [Fact]
    public void A_suggested_name_carries_the_moment_and_survives_a_filesystem()
    {
        var name = WindowsClipboard.SuggestName(new DateTime(2026, 9, 1, 14, 5, 9), ".png");

        Assert.Equal("clip 2026-09-01 140509.png", name);
        Assert.DoesNotContain(name, Path.GetInvalidFileNameChars().Contains);
    }
}

public sealed class SaucerHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "saucer-" + Guid.NewGuid().ToString("N")[..10]);

    public SaucerHistoryTests() => Directory.CreateDirectory(_root);

    private SaucerViewModel Model()
    {
        var model = new SaucerViewModel(new FakeHost(Path.Combine(_root, "hostdata")));
        model.SetIntakeFolder(Path.Combine(_root, "intake"));
        return model;
    }

    private static Clipping Text(string text) => new(ClippingKind.Text, text, null);

    [Fact]
    public void What_is_copied_turns_up_newest_first()
    {
        var model = Model();

        model.Add(Text("one"));
        model.Add(Text("two"));

        Assert.Equal(2, model.Clips.Count);
        Assert.Equal("two", model.Clips[0].Clipping.Text);
        Assert.Equal("one", model.Clips[1].Clipping.Text);
    }

    [Fact]
    public void The_same_thing_copied_twice_is_one_clipping()
    {
        var model = Model();

        model.Add(Text("same"));
        model.Add(Text("same"));

        // Copying again, or a program rewriting the clipboard with what is already there,
        // should not fill the list with the same line.
        Assert.Single(model.Clips);
    }

    [Fact]
    public void Copying_something_else_and_back_again_does_count()
    {
        var model = Model();

        model.Add(Text("a"));
        model.Add(Text("b"));
        model.Add(Text("a"));

        Assert.Equal(3, model.Clips.Count);
    }

    [Fact]
    public void The_oldest_falls_off_once_it_is_full()
    {
        var model = Model();

        for (var i = 0; i < 60; i++)
            model.Add(Text($"line {i}"));

        Assert.Equal(40, model.Clips.Count);
        Assert.Equal("line 59", model.Clips[0].Clipping.Text);
        Assert.DoesNotContain(model.Clips, c => c.Clipping.Text == "line 0");
    }

    [Fact]
    public void A_pinned_clipping_is_never_pushed_out()
    {
        var model = Model();
        model.Add(Text("keep me"));
        model.Clips[0].IsPinned = true;

        for (var i = 0; i < 60; i++)
            model.Add(Text($"line {i}"));

        Assert.Contains(model.Clips, c => c.Clipping.Text == "keep me");
    }

    [Fact]
    public void Forgetting_one_takes_it_out()
    {
        var model = Model();
        model.Add(Text("a"));
        model.Add(Text("b"));

        model.ForgetCommand.Execute(model.Clips[0]);

        Assert.Single(model.Clips);
        Assert.Equal("a", model.Clips[0].Clipping.Text);
    }

    [Fact]
    public void Clearing_empties_it()
    {
        var model = Model();
        model.Add(Text("a"));
        model.Add(Text("b"));

        model.ClearCommand.Execute(null);

        Assert.Empty(model.Clips);
        Assert.True(model.IsEmpty);
        Assert.Null(model.Selected);
    }

    [Fact]
    public void Text_is_never_saveable_because_saving_is_for_images()
    {
        var model = Model();
        model.Add(Text("just words"));

        Assert.False(model.SaveCommand.CanExecute(null));
        Assert.Null(model.Save(model.Clips[0]));
    }

    [Fact]
    public void Nothing_is_written_to_disk_just_by_copying()
    {
        var model = Model();
        var intake = Path.Combine(_root, "intake");

        model.Add(Text("a secret perhaps"));

        // History lives in memory. A clipboard ends up holding passwords, so the only thing
        // that writes anything is saving an image on purpose.
        Assert.False(Directory.Exists(intake) && Directory.EnumerateFileSystemEntries(intake).Any());
    }

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
}
