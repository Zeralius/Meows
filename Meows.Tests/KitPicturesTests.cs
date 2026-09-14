using Meows.Media;
using Meows.Plugins.Kit.Services;
using SkiaSharp;

namespace Meows.Tests;

/// <summary>
/// Kit's picture work: a token is the picture inside the ring and nothing outside it, a border
/// goes round a map without touching it, the grid guess picks a sensible number, and the
/// manifest follows the folder.
/// </summary>
public sealed class KitPicturesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kit-" + Guid.NewGuid().ToString("N")[..10]);

    public KitPicturesTests() => Directory.CreateDirectory(_root);

    /// <summary>A solid picture, so every pixel of it is opaque and coloured.</summary>
    private static SKBitmap Solid(int w, int h, SKColor colour)
    {
        var bitmap = new SKBitmap(w, h);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(colour);
        return bitmap;
    }

    [Fact]
    public void The_frames_that_ship_are_there_and_say_what_they_are()
    {
        Assert.Equal(4, Frames.Tokens.Count);
        Assert.Equal(3, Frames.Borders.Count);
        Assert.Contains(Frames.Tokens, t => t.File == "token-gold.png" && t.InnerRadius < t.Size / 2);
        Assert.Contains(Frames.Borders, b => b.File == "border-parchment.png" && b.Slice * 2 < b.Size);

        using var ring = Frames.Bitmap("token-gold.png");
        Assert.Equal(512, ring.Width);
    }

    [Fact]
    public void A_token_is_the_picture_inside_the_ring_and_transparent_everywhere_else()
    {
        using var source = Solid(800, 600, SKColors.Blue);
        var frame = Frames.Token("token-gold.png")!;

        var png = Pictures.MakeToken(source, frame, new TokenCrop());
        using var token = SKBitmap.Decode(png);

        Assert.Equal(512, token.Width);
        Assert.Equal(512, token.Height);

        // The corner is outside the ring: nothing there.
        Assert.Equal(0, token.GetPixel(2, 2).Alpha);
        Assert.Equal(0, token.GetPixel(509, 509).Alpha);
        // The middle is the picture.
        Assert.Equal(SKColors.Blue, token.GetPixel(256, 256));
        // Just inside the outer edge of the ring is the ring, not the picture and not nothing.
        var onRing = token.GetPixel(256, 512 - 20);
        Assert.Equal(255, onRing.Alpha);
        Assert.NotEqual(SKColors.Blue, onRing);
    }

    [Fact]
    public void Without_a_frame_the_token_is_still_round()
    {
        using var source = Solid(400, 400, SKColors.Red);

        using var token = SKBitmap.Decode(Pictures.MakeToken(source, null, new TokenCrop()));

        Assert.Equal(0, token.GetPixel(1, 1).Alpha);
        Assert.Equal(SKColors.Red, token.GetPixel(256, 256));
        Assert.Equal(SKColors.Red, token.GetPixel(256, 12));
    }

    [Fact]
    public void Zoom_and_offset_move_the_picture_inside_the_circle()
    {
        // Left half green, right half blue: at zoom 1 centred, the middle column is the seam.
        using var source = new SKBitmap(400, 400);
        using (var canvas = new SKCanvas(source))
        {
            canvas.Clear(SKColors.Green);
            canvas.DrawRect(200, 0, 200, 400, new SKPaint { Color = SKColors.Blue });
        }

        using var centred = SKBitmap.Decode(Pictures.MakeToken(source, null, new TokenCrop()));
        Assert.Equal(SKColors.Green, centred.GetPixel(200, 256));
        Assert.Equal(SKColors.Blue, centred.GetPixel(312, 256));

        // Pushed right by half the circle: the seam moves right, so the middle is now green.
        using var shifted = SKBitmap.Decode(Pictures.MakeToken(source, null, new TokenCrop(OffsetX: 0.5f)));
        Assert.Equal(SKColors.Green, shifted.GetPixel(312, 256));

        // Zoomed in twice: the seam stays in the middle, and the picture fills more than the circle.
        using var zoomed = SKBitmap.Decode(Pictures.MakeToken(source, null, new TokenCrop(Zoom: 2f)));
        Assert.Equal(SKColors.Green, zoomed.GetPixel(200, 256));
        Assert.Equal(SKColors.Blue, zoomed.GetPixel(312, 256));
    }

    [Fact]
    public void A_border_grows_the_canvas_and_leaves_the_picture_where_it_was()
    {
        using var source = Solid(300, 200, SKColors.Magenta);
        var frame = Frames.Border("border-stone.png")!;

        var (png, padding) = Pictures.AddBorder(source, frame);
        using var framed = SKBitmap.Decode(png);

        Assert.Equal(frame.Slice, padding);
        Assert.Equal(300 + 2 * padding, framed.Width);
        Assert.Equal(200 + 2 * padding, framed.Height);
        // The picture, untouched, one pixel inside the padding on every side.
        Assert.Equal(SKColors.Magenta, framed.GetPixel(padding, padding));
        Assert.Equal(SKColors.Magenta, framed.GetPixel(padding + 299, padding + 199));
        Assert.Equal(SKColors.Magenta, framed.GetPixel(padding + 150, padding + 100));
        // The border, where the frame is.
        Assert.NotEqual(SKColors.Magenta, framed.GetPixel(padding / 2, padding + 100));
        Assert.Equal(255, framed.GetPixel(padding / 2, padding + 100).Alpha);
    }

    [Fact]
    public void The_grid_guess_prefers_the_size_that_divides_both_sides_into_whole_squares()
    {
        Assert.Equal(70, Pictures.GuessGrid(2100, 1400));
        Assert.Equal(100, Pictures.GuessGrid(3000, 2000));
        // Every multiple of 140 is a multiple of 70, so a tie goes to the smaller square; the
        // guess is a starting point for the eye, not a verdict.
        Assert.Equal(70, Pictures.GuessGrid(4200, 2940));
        Assert.Equal(100, Pictures.GuessGrid(2500, 1500));
        Assert.Equal((30, 20), Pictures.Squares(3004, 2003, new GridSpec { Size = 100, OffsetX = 4, OffsetY = 3 }));
    }

    [Fact]
    public void The_manifest_follows_the_folder_and_a_map_starts_with_a_grid()
    {
        var kit = Path.Combine(_root, "Cellar of Woe");
        Directory.CreateDirectory(Path.Combine(kit, "maps"));
        Directory.CreateDirectory(Path.Combine(kit, "tokens"));
        Directory.CreateDirectory(Path.Combine(kit, "notes"));
        File.WriteAllBytes(Path.Combine(kit, "maps", "tavern.png"), Preparer.Encode(Solid(10, 10, SKColors.White), ImageFormat.Png, 100));
        File.WriteAllBytes(Path.Combine(kit, "tokens", "goblin.png"), Preparer.Encode(Solid(10, 10, SKColors.White), ImageFormat.Png, 100));
        File.WriteAllText(Path.Combine(kit, "notes", "run.md"), "# Run sheet");

        var manifest = KitManifest.Load(kit);
        Assert.Equal("Cellar of Woe", manifest.Title);
        Assert.True(manifest.Reconcile(kit));

        var map = Assert.Single(manifest.Maps);
        Assert.Equal("maps/tavern.png", map.File);
        Assert.Equal("tavern", map.Name);
        Assert.NotNull(map.Grid);
        Assert.True(map.Grid!.Enabled);
        Assert.Null(Assert.Single(manifest.Tokens).Grid);
        Assert.Equal(["notes/run.md"], manifest.Notes);

        manifest.Maps[0].Name = "The Prancing Pony";
        manifest.Maps[0].Grid!.Enabled = false;
        manifest.Save(kit);

        var again = KitManifest.Load(kit);
        Assert.Equal("The Prancing Pony", again.Maps[0].Name);
        Assert.False(again.Maps[0].Grid!.Enabled);
        Assert.False(again.Reconcile(kit));

        // A picture gone from the folder is gone from the manifest.
        File.Delete(Path.Combine(kit, "tokens", "goblin.png"));
        Assert.True(again.Reconcile(kit));
        Assert.Empty(again.Tokens);
    }

    [Fact]
    public void The_foundry_export_carries_the_numbers_and_the_gridless_decision()
    {
        var kit = Path.Combine(_root, "Night");
        Directory.CreateDirectory(Path.Combine(kit, "maps"));
        File.WriteAllBytes(Path.Combine(kit, "maps", "a.png"), Preparer.Encode(Solid(2100, 1400, SKColors.White), ImageFormat.Png, 100));
        File.WriteAllBytes(Path.Combine(kit, "maps", "b.png"), Preparer.Encode(Solid(700, 700, SKColors.White), ImageFormat.Png, 100));
        var manifest = new KitManifest
        {
            Title = "Night",
            Maps =
            [
                new KitItem { File = "maps/a.png", Name = "Tavern", Width = 2100, Height = 1400, Grid = new GridSpec { Size = 70 } },
                new KitItem { File = "maps/b.png", Name = "Dream", Width = 700, Height = 700, Grid = new GridSpec { Enabled = false } },
            ],
        };

        var report = Exporter.ForFoundry(kit, manifest, Path.Combine(_root, "out"));

        Assert.Equal(3, report.Files);
        var json = File.ReadAllText(Path.Combine(report.Folder, "kit.json"));
        Assert.Contains("\"columns\": 30", json);
        Assert.Contains("\"rows\": 20", json);
        Assert.Contains("\"gridless\": true", json);
        Assert.Contains("\"meowsKit\": 1", json);
        Assert.True(File.Exists(Path.Combine(report.Folder, "maps", "a.png")));
    }

    [Fact]
    public void The_roll20_export_sizes_a_gridded_map_to_whole_units_and_writes_the_sheet()
    {
        var kit = Path.Combine(_root, "Units");
        Directory.CreateDirectory(Path.Combine(kit, "maps"));
        File.WriteAllBytes(Path.Combine(kit, "maps", "a.png"), Preparer.Encode(Solid(3000, 2000, SKColors.White), ImageFormat.Png, 100));
        var manifest = new KitManifest
        {
            Title = "Units",
            Maps = [new KitItem { File = "maps/a.png", Name = "Tavern", Width = 3000, Height = 2000, Grid = new GridSpec { Size = 100 } }],
        };

        var report = Exporter.ForRoll20(kit, manifest, Path.Combine(_root, "out"), 10_000_000);

        var jpeg = Path.Combine(report.Folder, "01 Tavern.jpg");
        Assert.True(File.Exists(jpeg));
        using var bitmap = SKBitmap.Decode(jpeg);
        Assert.Equal(30 * Exporter.Roll20Unit, bitmap.Width);
        Assert.Equal(20 * Exporter.Roll20Unit, bitmap.Height);

        var sheet = File.ReadAllText(Path.Combine(report.Folder, "ROLL20.md"));
        Assert.Contains("30 × 20 units", sheet);
        Assert.Contains("01 Tavern.jpg", sheet);
        Assert.Contains("no way in", sheet);
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
