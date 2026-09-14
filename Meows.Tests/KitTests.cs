using Meows.Media;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Kit.Services;
using Meows.Plugins.Kit.ViewModels;
using SkiaSharp;

namespace Meows.Tests;

/// <summary>
/// The Kit tab: a folder per one-shot, pictures in it named, gridded or not, cut and framed,
/// and written out. The picture work has its own tests; these are the tab's verbs.
/// </summary>
public sealed class KitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kittab-" + Guid.NewGuid().ToString("N")[..10]);

    public KitTests() => Directory.CreateDirectory(_root);

    private static byte[] Png(int w, int h, SKColor colour)
    {
        using var bitmap = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bitmap))
            canvas.Clear(colour);
        return Preparer.Encode(bitmap, ImageFormat.Png, 100);
    }

    private (KitViewModel Model, FakeHost Host) Open()
    {
        var host = new FakeHost(Path.Combine(_root, "hostdata"));
        host.SaveSettings(new KitSettings { Root = Path.Combine(_root, "Oneshots") });
        return (new KitViewModel(host), host);
    }

    [Fact]
    public void A_new_kit_is_a_folder_with_the_four_folders_and_a_manifest()
    {
        var (model, host) = Open();

        model.NewKitName = "Cellar of Woe";
        model.NewKitCommand.Execute(null);

        var kit = Assert.Single(model.Kits);
        Assert.Equal("Cellar of Woe", kit.Name);
        Assert.Same(kit, model.SelectedKit);
        foreach (var sub in new[] { "maps", "tokens", "handouts", "notes" })
            Assert.True(Directory.Exists(Path.Combine(kit.Folder, sub)));
        Assert.True(File.Exists(Path.Combine(kit.Folder, KitManifest.FileName)));
        Assert.Contains(host.Store.Events, e => e.Kind == "created");
    }

    [Fact]
    public void Pictures_added_as_maps_get_a_grid_guess_and_a_thumbnail_row()
    {
        var (model, _) = Open();
        model.NewKitName = "Night";
        model.NewKitCommand.Execute(null);
        var source = Path.Combine(_root, "tavern.png");
        File.WriteAllBytes(source, Png(2100, 1400, SKColors.SandyBrown));

        model.AddAs = ItemKind.Map;
        model.AddPictures([source]);

        var item = Assert.Single(model.Items);
        Assert.True(item.IsMap);
        Assert.Equal("tavern", item.Name);
        Assert.Equal(2100, item.Item.Width);
        Assert.Equal(70, item.Item.Grid!.Size);
        Assert.Contains("30 × 20 squares", item.Detail);
        // Copied in, never moved: the source is still where it was.
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void Renaming_changes_the_name_and_the_file_and_refuses_a_clash()
    {
        var (model, _) = Open();
        model.NewKitName = "Night";
        model.NewKitCommand.Execute(null);
        var maps = Path.Combine(model.SelectedKit!.Folder, "maps");
        File.WriteAllBytes(Path.Combine(maps, "a.png"), Png(100, 100, SKColors.White));
        File.WriteAllBytes(Path.Combine(maps, "taken.png"), Png(100, 100, SKColors.White));
        model.RefreshCommand.Execute(null);
        model.Selected = model.Items.First(i => i.Name == "a");

        model.EditName = "The Prancing Pony";
        model.RenameCommand.Execute(null);

        Assert.Equal("The Prancing Pony", model.Selected!.Name);
        Assert.True(File.Exists(Path.Combine(maps, "The Prancing Pony.png")));
        Assert.False(File.Exists(Path.Combine(maps, "a.png")));
        Assert.Equal("The Prancing Pony", KitManifest.Load(model.SelectedKit.Folder).Maps.First(m => m.File.Contains("Prancing")).Name);

        model.EditName = "taken";
        model.RenameCommand.Execute(null);
        Assert.True(model.HasError);
        Assert.Contains("taken.png", model.ErrorMessage);
        Assert.True(File.Exists(Path.Combine(maps, "The Prancing Pony.png")));
    }

    [Fact]
    public void A_map_can_be_gridless_and_the_manifest_remembers()
    {
        var (model, _) = Open();
        model.NewKitName = "Night";
        model.NewKitCommand.Execute(null);
        File.WriteAllBytes(Path.Combine(model.SelectedKit!.Folder, "maps", "dream.png"), Png(700, 700, SKColors.White));
        model.RefreshCommand.Execute(null);
        model.Selected = model.Items.Single();

        Assert.True(model.GridEnabled);
        model.GridEnabled = false;

        Assert.Contains("Gridless", model.GridText);
        Assert.Contains("no grid", model.Selected!.Detail);
        Assert.False(KitManifest.Load(model.SelectedKit.Folder).Maps.Single().Grid!.Enabled);

        model.GridEnabled = true;
        model.GridSize = 70;
        model.ApplyGridCommand.Execute(null);
        Assert.Contains("10 × 10 squares", model.GridText);
    }

    [Fact]
    public async Task Making_a_token_writes_a_round_png_and_keeps_the_original()
    {
        var (model, host) = Open();
        model.NewKitName = "Night";
        model.NewKitCommand.Execute(null);
        var tokens = Path.Combine(model.SelectedKit!.Folder, "tokens");
        File.WriteAllBytes(Path.Combine(tokens, "goblin.jpg"), Preparer.Encode(SKBitmap.Decode(Png(300, 400, SKColors.Green)), ImageFormat.Jpeg, 90));
        model.RefreshCommand.Execute(null);
        model.Selected = model.Items.Single();
        Assert.True(model.Selected!.IsToken);

        model.Ring = model.Rings.First(r => r.File == "token-crimson.png");
        model.MakeTokenCommand.Execute(null);
        await WaitUntil(() => !model.IsBusy && model.Selected.FileName.EndsWith(".png"));

        var made = Path.Combine(tokens, "goblin.png");
        Assert.True(File.Exists(made));
        Assert.False(File.Exists(Path.Combine(tokens, "goblin.jpg")));
        Assert.True(File.Exists(Path.Combine(model.SelectedKit.Folder, "originals", "goblin.jpg")));
        using var bitmap = SKBitmap.Decode(made);
        Assert.Equal(512, bitmap.Width);
        Assert.Equal(0, bitmap.GetPixel(3, 3).Alpha);
        Assert.Equal(512, model.Selected.Item.Width);
        Assert.Equal("token-crimson.png", model.Selected.Item.Frame);
        Assert.Contains(host.Store.Events, e => e.Kind == "token");
    }

    [Fact]
    public async Task Framing_a_map_adds_padding_and_the_grid_stays_put()
    {
        var (model, _) = Open();
        model.NewKitName = "Night";
        model.NewKitCommand.Execute(null);
        File.WriteAllBytes(Path.Combine(model.SelectedKit!.Folder, "maps", "cellar.png"), Png(1000, 700, SKColors.White));
        model.RefreshCommand.Execute(null);
        model.Selected = model.Items.Single();
        model.GridSize = 100;
        model.ApplyGridCommand.Execute(null);

        model.Border = model.Borders.First(b => b.File == "border-parchment.png");
        model.FrameCommand.Execute(null);
        await WaitUntil(() => !model.IsBusy && model.Selected!.Item.Padding > 0);

        var item = model.Selected!.Item;
        Assert.Equal(1000 + 2 * item.Padding, item.Width);
        Assert.Equal("border-parchment.png", item.Frame);
        // Ten by seven squares, as before the frame: the padding is outside the map.
        Assert.Contains("10 × 7 squares", model.GridText);
    }

    [Fact]
    public async Task The_foundry_export_lands_in_the_exports_folder_with_a_manifest()
    {
        var (model, host) = Open();
        model.NewKitName = "Night";
        model.NewKitCommand.Execute(null);
        File.WriteAllBytes(Path.Combine(model.SelectedKit!.Folder, "maps", "a.png"), Png(700, 700, SKColors.White));
        model.RefreshCommand.Execute(null);

        model.ExportFoundryCommand.Execute(null);
        await WaitUntil(() => !model.IsBusy && model.Status.Contains("written"));

        var folder = Path.Combine(model.ExportRoot, "Night");
        Assert.True(File.Exists(Path.Combine(folder, "kit.json")));
        Assert.True(File.Exists(Path.Combine(folder, "maps", "a.png")));
        Assert.Contains(host.Store.Events, e => e.Kind == "exported-foundry");
        Assert.Contains(host.Lines, l => l.StartsWith("post: Kit written"));
    }

    [Fact]
    public void A_handoff_of_files_adds_them_as_whatever_is_chosen_and_answers()
    {
        var (model, _) = Open();
        model.NewKitName = "Night";
        model.NewKitCommand.Execute(null);
        var source = Path.Combine(_root, "letter.png");
        File.WriteAllBytes(source, Png(200, 300, SKColors.White));
        model.AddAs = ItemKind.Handout;
        string? heard = null;

        Assert.True(model.Accepts(Handoff.Files([source])));
        model.Receive(Handoff.Files([source]) with { Reply = o => heard = o });

        Assert.True(model.Items.Single().IsHandout);
        Assert.Equal("1 added to Night", heard);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(50);
        Assert.True(condition(), "timed out waiting");
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
