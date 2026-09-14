using Meows.Bot;
using Meows.Media;
using Meows.Plugins.Kibble.Services;
using Meows.Plugins.Kibble.ViewModels;
using Meows.Plugins.Portion.ViewModels;
using Meows.Plugins.TelegramPoster.ViewModels;
using SkiaSharp;

namespace Meows.Tests;

/// <summary>
/// A file the bot would fail on has a folder now, Held_Back beside To_Send, and two ways there:
/// shrunk if it is a picture that is merely too big, held back otherwise. From Kibble at the
/// click, from Portion after a walk, from Telegram Poster on a queue row.
/// </summary>
public sealed class HeldBackTests : IDisposable
{
    private readonly TempWorkspace _temp = new();
    private readonly string _intake;

    public HeldBackTests()
    {
        _temp.WriteConfig(_temp.AddGroup("Alpha"));
        _intake = Path.Combine(_temp.Root, "intake");
        Directory.CreateDirectory(_intake);

        // Plain deletion here. The app sends originals to the Recycle Bin, and a test suite
        // that filled the bin with noise pictures would not be thanked for it.
        Slimmer.RemoveOriginal = path =>
        {
            File.Delete(path);
            return null;
        };
    }

    private GroupConfig Alpha => _temp.Workspace.LoadConfig().Groups[0];

    private static byte[] NoisyPng(int side)
    {
        using var bitmap = new SKBitmap(side, side);
        var random = new Random(3);
        for (var y = 0; y < side; y++)
        for (var x = 0; x < side; x++)
            bitmap.SetPixel(x, y, new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
        return Preparer.Encode(bitmap, ImageFormat.Png, 100);
    }

    private string Write(string folder, string name, byte[] bytes)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private KibbleViewModel OpenKibble()
    {
        var model = new KibbleViewModel(new FakeHost(Path.Combine(_temp.Root, "hostdata")));
        model.SetBotRoot(_temp.Workspace.Root);
        model.LoadFolder(_intake);
        return model;
    }

    [Fact]
    public void Held_Back_sits_beside_the_queue_and_keeps_the_file_and_its_date()
    {
        var path = Write(_intake, "clip.mp4", new byte[10]);
        var written = new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, written);

        var outcome = HeldBack.SetAside(_temp.Workspace, Alpha, path);

        Assert.True(outcome.Ok);
        Assert.Equal(Path.Combine(_temp.Workspace.GroupFolder(Alpha), "Held_Back", "clip.mp4"), outcome.Destination);
        Assert.False(File.Exists(path));
        Assert.Equal(written, File.GetLastWriteTimeUtc(outcome.Destination!));

        // Beside To_Send, never inside it: the bot lists a queue without recursing today, and
        // one change to that would post everything set aside.
        Assert.Empty(_temp.Workspace.Scan(_temp.Workspace.ToSendFolder(Alpha)));

        // A second file of the same name is not written over the first.
        var again = Write(_intake, "clip.mp4", new byte[20]);
        var second = HeldBack.SetAside(_temp.Workspace, Alpha, again);
        Assert.EndsWith("clip (2).mp4", second.Destination);

        Assert.True(HeldBack.PutBack(outcome.Destination!, path));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Kibble_refuses_an_oversize_video_and_offers_to_hold_it_not_to_shrink_it()
    {
        // A video needs ffmpeg to shrink, which is a different tool. Holding is the way out.
        Write(_intake, "clip.mp4", new byte[MediaRules.ByteLimit(MediaKind.Video)!.Value + 1]);
        var model = OpenKibble();
        model.SetSelection([model.Incoming[0]]);

        model.SendToCommand.Execute(model.Destinations[0]);

        Assert.True(model.IsBlocked);
        Assert.True(model.CanHoldBlocked);
        Assert.False(model.CanShrinkBlocked);

        model.HoldCommand.Execute(null);

        Assert.False(model.IsBlocked);
        Assert.Empty(model.Incoming);
        Assert.True(File.Exists(Path.Combine(_temp.Workspace.HeldBackFolder(Alpha), "clip.mp4")));
        Assert.Empty(_temp.Workspace.Scan(_temp.Workspace.ToSendFolder(Alpha)));
        Assert.Contains("Held_Back", model.StatusMessage);

        // Held, not gone: Undo brings it back into the grid.
        Assert.Equal(1, model.UndoableCount);
        model.UndoCommand.Execute(null);
        Assert.Single(model.Incoming);
        Assert.True(File.Exists(Path.Combine(_intake, "clip.mp4")));
    }

    [Fact]
    public async Task Kibble_shrinks_a_picture_that_is_merely_too_big_and_sends_the_result()
    {
        var bytes = NoisyPng(2000);
        Assert.True(bytes.LongLength > MediaRules.PhotoLimitBytes, $"test picture is only {bytes.Length} bytes");
        Write(_intake, "big.png", bytes);
        var model = OpenKibble();
        model.SetSelection([model.Incoming[0]]);

        model.SendToCommand.Execute(model.Destinations[0]);
        Assert.True(model.IsBlocked);
        Assert.True(model.CanShrinkBlocked);
        Assert.True(model.CanHoldBlocked);

        model.ShrinkAndSendCommand.Execute(null);
        await WaitUntil(() => !model.IsBlocked && model.Incoming.Count == 0);

        var queued = Assert.Single(_temp.Workspace.Scan(_temp.Workspace.ToSendFolder(Alpha)));
        Assert.True(new FileInfo(queued).Length <= MediaRules.PhotoLimitBytes);
        Assert.False(File.Exists(Path.Combine(_intake, "big.png")));
        Assert.Contains("Shrunk and sent", model.StatusMessage);
        Assert.Equal(1, model.UndoableCount);
    }

    [Fact]
    public void The_group_card_counts_what_is_held_and_opens_the_folder_to_decide_about_it()
    {
        Write(_intake, "clip.mp4", new byte[MediaRules.ByteLimit(MediaKind.Video)!.Value + 1]);
        var model = OpenKibble();
        model.SetSelection([model.Incoming[0]]);
        model.SendToCommand.Execute(model.Destinations[0]);
        model.HoldCommand.Execute(null);

        var alpha = model.Destinations[0];
        Assert.True(alpha.HasHeld);
        Assert.Equal("1 held back", alpha.HeldText);

        // Open the folder as the one to sort: the held file is back in the grid, from there.
        model.OpenHeldCommand.Execute(alpha);
        Assert.Equal(alpha.HeldBackFolder, model.SourceFolder);
        Assert.Single(model.Incoming);

        // Still too big, still refused, but holding it back from Held_Back is going nowhere.
        model.SetSelection([model.Incoming[0]]);
        model.SendToCommand.Execute(alpha);
        Assert.True(model.IsBlocked);
        Assert.False(model.CanHoldBlocked);
    }

    [Fact]
    public void A_duplicate_gets_neither_way_out_because_it_has_a_folder_of_its_own()
    {
        var bytes = TestPictures.Jpeg(400, 300);
        Directory.CreateDirectory(_temp.Workspace.ToSendFolder(Alpha));
        Write(_temp.Workspace.ToSendFolder(Alpha), "same.jpg", bytes);
        Write(_intake, "same.jpg", bytes);
        var model = OpenKibble();
        model.DuplicatesToFolder = false;
        model.SetSelection([model.Incoming[0]]);

        model.SendToCommand.Execute(model.Destinations[0]);

        Assert.True(model.IsBlocked);
        Assert.False(model.CanHoldBlocked);
        Assert.False(model.CanShrinkBlocked);
    }

    [Fact]
    public void Portion_holds_back_what_it_cannot_shrink()
    {
        var queue = _temp.Workspace.ToSendFolder(Alpha);
        Directory.CreateDirectory(queue);
        var clip = Write(queue, "clip.mp4", new byte[MediaRules.ByteLimit(MediaKind.Video)!.Value + 1]);
        var host = new FakeHost(Path.Combine(_temp.Root, "hostdata"));
        var model = new PortionViewModel(host);
        model.SetBotRoot(_temp.Workspace.Root);

        // The weigh runs as background work; the fake host does not run it, so weigh by hand.
        model.Receive(Plugins.Abstractions.Handoff.Files([clip]));
        var row = Assert.Single(model.Heavies);
        Assert.True(row.WillFail);
        Assert.False(row.CanShrink);
        model.Selected = row;

        model.HoldCommand.Execute(null);

        Assert.Empty(model.Heavies);
        Assert.False(File.Exists(clip));
        Assert.True(File.Exists(Path.Combine(_temp.Workspace.HeldBackFolder(Alpha), "clip.mp4")));
        Assert.Contains(host.Store.Events, e => e.Kind == "held");
    }

    [Fact]
    public void Portion_takes_files_handed_over_and_says_which_are_fine()
    {
        var queue = _temp.Workspace.ToSendFolder(Alpha);
        Directory.CreateDirectory(queue);
        var fine = Write(queue, "fine.jpg", TestPictures.Jpeg(400, 300));
        var clip = Write(queue, "clip.mp4", new byte[MediaRules.ByteLimit(MediaKind.Video)!.Value + 1]);
        var model = new PortionViewModel(new FakeHost(Path.Combine(_temp.Root, "hostdata")));
        model.SetBotRoot(_temp.Workspace.Root);

        Assert.True(model.Accepts(Plugins.Abstractions.Handoff.Files([fine, clip])));
        model.Receive(Plugins.Abstractions.Handoff.Files([fine, clip]));

        Assert.Single(model.Heavies);
        Assert.Equal("clip.mp4", model.Selected?.FileName);
        Assert.Contains("1 are fine", model.Status);
    }

    [Fact]
    public async Task A_queue_row_in_Telegram_Poster_carries_the_verdict()
    {
        var queue = _temp.Workspace.ToSendFolder(Alpha);
        Directory.CreateDirectory(queue);
        var clip = Write(queue, "clip.mp4", new byte[MediaRules.ByteLimit(MediaKind.Video)!.Value + 1]);
        var fine = Write(queue, "fine.jpg", TestPictures.Jpeg(400, 300));

        var heavy = new MediaItemViewModel(clip);
        var light = new MediaItemViewModel(fine);
        await heavy.WeighAsync(Alpha, CancellationToken.None);
        await light.WeighAsync(Alpha, CancellationToken.None);

        Assert.True(heavy.WillFail);
        Assert.False(heavy.CanShrink);
        Assert.Contains("MB limit", heavy.TroubleText);
        Assert.False(light.WillFail);
        Assert.Null(light.Trouble);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
            await Task.Delay(50);
        Assert.True(condition(), "timed out waiting");
    }

    public void Dispose() => _temp.Dispose();
}
