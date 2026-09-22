using Meows.Plugins.Abstractions;
using Meows.Plugins.Chonk.ViewModels;
using Meows.Services;
using Meows.ViewModels;

namespace Meows.Tests;

/// <summary>
/// The 3.0.0 shell pieces that have logic worth pinning without a window: the palette's command
/// mode and scope, a plugin picking a folder through the host, and one Meows telling another
/// that it is already here.
/// </summary>
public sealed class ShellKeysAndPicksTests
{
    private static PaletteItem Item(string title, bool command = false, bool only = false) =>
        new("•", title, "", () => { }) { IsCommand = command, CommandOnly = only };

    [Fact]
    public void A_query_starting_with_a_mark_lists_commands_only()
    {
        var fixedItems = new[] { Item("Go to Home"), Item("Dark theme", command: true), Item("Pop out History", command: true, only: true) };
        var palette = new CommandPaletteViewModel(() => fixedItems, _ => []);

        palette.Open();
        Assert.Equal(["Dark theme", "Go to Home"], palette.Items.Select(i => i.Title));

        palette.Query = ">";
        Assert.Equal(["Dark theme", "Pop out History"], palette.Items.Select(i => i.Title));

        palette.Query = "> pop";
        Assert.Equal(["Pop out History"], palette.Items.Select(i => i.Title));
    }

    [Fact]
    public void Scoped_to_a_tab_the_palette_asks_only_the_search_and_says_which_tab()
    {
        var asked = new List<string>();
        var palette = new CommandPaletteViewModel(() => [Item("Go to Home")], q => { asked.Add(q); return [Item("hit " + q)]; });

        palette.OpenScoped("meows.collar", "Collar");

        Assert.True(palette.IsScoped);
        Assert.Equal("meows.collar", palette.ScopeKey);
        Assert.Contains("Collar", palette.Hint);
        Assert.Empty(palette.Items);

        palette.Query = "warranty";
        Assert.Equal(["warranty"], asked);
        Assert.Equal(["hit warranty"], palette.Items.Select(i => i.Title));

        palette.IsOpen = false;
        palette.Open();
        Assert.False(palette.IsScoped);
        Assert.Equal(["Go to Home"], palette.Items.Select(i => i.Title));
    }

    [Fact]
    public async Task A_plugin_picks_a_folder_through_the_host_and_a_cancel_changes_nothing()
    {
        var root = Path.Combine(Path.GetTempPath(), "picks-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            var host = new FakeHost(Path.Combine(root, "host"));
            var model = new ChonkViewModel(host);

            // Chonk starts on a drive of its own choosing; a cancelled dialog leaves that alone.
            var before = model.SelectedRoot;
            host.Picks.Answers.Enqueue(null);
            model.PickFolderCommand.Execute(null);
            await Task.Delay(50);
            Assert.Equal(before, model.SelectedRoot);

            host.Picks.Answers.Enqueue(root);
            model.PickFolderCommand.Execute(null);
            await Task.Delay(50);
            Assert.Equal(root, model.SelectedRoot);
            Assert.Equal(["folder", "folder"], host.Picks.Asked);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (Exception) { }
        }
    }

    [Theory]
    [InlineData(100, 100, true)]        // well inside the first screen
    [InlineData(1900, 50, true)]        // straddling into the second screen, title bar on it
    [InlineData(-1300, 100, false)]     // left of everything
    [InlineData(500, -200, false)]      // title bar above the top: nothing to grab
    [InlineData(4000, 100, false)]      // right of the second screen
    [InlineData(500, 1050, false)]      // below the first screen's working area
    public void A_remembered_place_is_used_only_when_the_window_can_be_grabbed(int x, int y, bool fits)
    {
        var screens = new[]
        {
            new Avalonia.PixelRect(0, 0, 1920, 1040),
            new Avalonia.PixelRect(1920, 0, 1920, 1040),
        };

        Assert.Equal(fits, WindowLayout.Fits(new WindowPlace(x, y, 1400, 880), screens));
    }

    [Fact]
    public void With_no_screens_to_ask_the_place_is_taken_as_it_is()
    {
        Assert.True(WindowLayout.Fits(new WindowPlace(-5000, -5000, 100, 100), []));
    }

    [Fact]
    public void The_second_start_reaches_the_first_with_its_arguments()
    {
        using var first = SingleInstance.TryClaim();
        if (first is null)
            return; // A real Meows is running on this machine and holds the name; nothing to prove here.

        // The name is taken, so the same process cannot claim it again.
        Assert.Null(SingleInstance.TryClaim());

        var heard = new TaskCompletionSource<string[]>();
        first.Listen(args => heard.TrySetResult(args));

        Assert.True(SingleInstance.Signal(["--open", "meows.collar"]));
        Assert.True(heard.Task.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(["--open", "meows.collar"], heard.Task.Result);
    }
}
