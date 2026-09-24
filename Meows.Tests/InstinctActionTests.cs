using Meows.Plugins.Abstractions;
using Meows.Plugins.Collar;
using Meows.Plugins.Collar.ViewModels;
using Meows.Plugins.Portion;
using Meows.Plugins.Portion.ViewModels;
using Meows.Plugins.Purrge;
using Meows.Plugins.Purrge.ViewModels;
using Meows.Plugins.Scruff;
using Meows.Plugins.Scruff.ViewModels;
using Meows.Plugins.WeighIn;
using Meows.Plugins.WeighIn.ViewModels;

namespace Meows.Tests;

/// <summary>
/// What each plugin does when a rule asks. The fake host runs no background work, so for the
/// actions that walk something the test sees the walk being asked for and then calls it off,
/// which is also what a quit does; the answer at the end of a real walk is the tab's own
/// status line, pinned by each plugin's own tests.
/// </summary>
public sealed class InstinctActionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "meows-actions-" + Guid.NewGuid().ToString("N"));

    public InstinctActionTests() => Directory.CreateDirectory(_root);

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

    private FakeHost Host(string name) => new(Path.Combine(_root, "host-" + name));

    private static ActionRequest Asked(string action, string subject, string? detail = null, Dictionary<string, string>? data = null) =>
        new(action, new StoredEvent(7, DateTime.Now, "meows.birdwatch", "saved", subject, detail, data ?? []));

    [Fact]
    public void Every_shipped_action_and_recorded_kind_has_words_in_both_languages()
    {
        var english = TestStrings.Load();
        foreach (var type in ShippedPlugins.Types)
        {
            var plugin = (IMeowsPlugin)Activator.CreateInstance(type)!;
            Assert.Equal(plugin.Actions.Count, plugin.Actions.Select(a => a.Id).Distinct().Count());
            foreach (var action in plugin.Actions)
            {
                Assert.NotEqual(action.Label, english[action.Label]);
                if (action.Description is { } description)
                    Assert.NotEqual(description, english[description]);
            }
            foreach (var kind in plugin.Records)
                Assert.NotEqual(kind.Label, english[kind.Label]);
        }
    }

    [Fact]
    public void Every_plugin_that_offers_an_action_has_a_view_model_that_performs_it()
    {
        Type[] performers =
        [
            typeof(CollarViewModel), typeof(PortionViewModel), typeof(PurrgeViewModel),
            typeof(WeighInViewModel), typeof(ScruffViewModel),
        ];
        var offering = ShippedPlugins.Types
            .Select(t => (IMeowsPlugin)Activator.CreateInstance(t)!)
            .Where(p => p.Actions.Count > 0)
            .Select(p => p.Id)
            .Order()
            .ToList();

        Assert.Equal(["meows.collar", "meows.portion", "meows.purrge", "meows.scruff", "meows.weighin"], offering);
        Assert.All(performers, t => Assert.True(typeof(IActionTarget).IsAssignableFrom(t), t.Name));
    }

    [Fact]
    public async Task Collar_puts_it_on_the_list_once_with_the_file_and_the_other_plugins_words()
    {
        var host = Host("collar");
        using var model = new CollarViewModel(host);
        var receipt = Path.Combine(_root, "receipt.pdf");
        File.WriteAllText(receipt, "paper");

        var said = await model.Perform(Asked(CollarPlugin.RemindToday, receipt, "From @someone"), CancellationToken.None);

        var entry = Assert.Single(model.Entries).Entry;
        Assert.Equal("receipt.pdf", entry.Title);
        Assert.Equal(DateTime.Today, entry.Due);
        Assert.Equal(receipt, entry.File);
        Assert.Equal("From @someone", entry.Note);
        Assert.Contains("receipt.pdf", said);
        Assert.Null(model.Selected);

        var again = await model.Perform(Asked(CollarPlugin.RemindToday, receipt), CancellationToken.None);
        Assert.Single(model.Entries);
        Assert.Contains("already", again);

        await model.Perform(Asked(CollarPlugin.RemindWeek, "Check the big set in F:"), CancellationToken.None);
        var later = Assert.Single(model.Entries, e => e.Entry.Title == "Check the big set in F:").Entry;
        Assert.Equal(DateTime.Today.AddDays(7), later.Due);
        Assert.Null(later.File);

        // Kept, not just shown: a Collar opened tomorrow still has them.
        using var reopened = new CollarViewModel(host);
        Assert.Equal(2, reopened.Entries.Count);

        await Assert.ThrowsAsync<ActionDeclinedException>(() => model.Perform(Asked("juggle", receipt), CancellationToken.None));
    }

    [Fact]
    public async Task Portion_weighs_the_queues_when_asked_and_stops_when_called_off()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteConfig(workspace.AddGroup("Alpha"));
        var host = Host("portion");
        var model = new PortionViewModel(host);
        model.SetBotRoot(workspace.Workspace.Root);
        var before = host.Work.Requested.Count(t => t == "Weighing the bot's queues");

        using var stop = new CancellationTokenSource();
        var asked = model.Perform(Asked(PortionPlugin.CheckAction, "anything"), stop.Token);

        Assert.Equal(before + 1, host.Work.Requested.Count(t => t == "Weighing the bot's queues"));
        Assert.False(asked.IsCompleted);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asked);

        await Assert.ThrowsAsync<ActionDeclinedException>(() => model.Perform(Asked("juggle", "x"), CancellationToken.None));
    }

    [Fact]
    public async Task Purrge_walks_the_folder_of_the_file_and_declines_what_is_not_there()
    {
        var host = Host("purrge");
        using var model = new PurrgeViewModel(host);
        var folder = Path.Combine(_root, "pile");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "a.jpg");
        File.WriteAllBytes(file, TestPictures.Jpeg());

        var gone = await Assert.ThrowsAsync<ActionDeclinedException>(() =>
            model.Perform(Asked(PurrgePlugin.ScanAction, Path.Combine(_root, "missing.jpg")), CancellationToken.None));
        Assert.Contains("missing.jpg", gone.Message);

        using var stop = new CancellationTokenSource();
        var asked = model.Perform(Asked(PurrgePlugin.ScanAction, file), stop.Token);

        Assert.Equal(folder, model.ScanRoot);
        Assert.Contains(host.Work.Requested, t => t == "Scanning pile");
        await Assert.ThrowsAsync<ActionDeclinedException>(() => model.Perform(Asked(PurrgePlugin.ScanAction, file), CancellationToken.None));

        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asked);
    }

    [Fact]
    public async Task Weigh_in_takes_one_reading_for_however_many_rules_ask_while_it_runs()
    {
        var host = Host("weighin");
        var folder = Path.Combine(host.DataDirectory, "readings");
        Plugins.WeighIn.Services.Readings.Save(folder, new Plugins.WeighIn.Services.Reading(DateTime.Now.AddDays(-1), []));
        using var model = new WeighInViewModel(host);
        Assert.DoesNotContain(host.Work.Requested, t => t == "Reading the drives");

        using var stop = new CancellationTokenSource();
        var first = model.Perform(Asked(WeighInPlugin.MeasureAction, @"F:\"), stop.Token);
        var second = model.Perform(Asked(WeighInPlugin.MeasureAction, @"G:\"), stop.Token);

        Assert.Single(host.Work.Requested, t => t == "Reading the drives");
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
    }

    [Fact]
    public async Task Scruff_leaves_a_picture_that_carries_nothing_exactly_as_it_was_and_refuses_what_is_not_one()
    {
        var host = Host("scruff");
        using var model = new ScruffViewModel(host);
        var picture = Path.Combine(_root, "plain.jpg");
        File.WriteAllBytes(picture, TestPictures.Jpeg());
        var stamp = new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(picture, stamp);
        var bytes = File.ReadAllBytes(picture);

        var said = await model.Perform(Asked(ScruffPlugin.CleanAction, picture), CancellationToken.None);

        Assert.Contains("carried nothing", said);
        Assert.Equal(bytes, File.ReadAllBytes(picture));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(picture));

        var text = Path.Combine(_root, "notes.txt");
        File.WriteAllText(text, "not a picture");
        await Assert.ThrowsAsync<ActionDeclinedException>(() => model.Perform(Asked(ScruffPlugin.CleanAction, text), CancellationToken.None));
        await Assert.ThrowsAsync<ActionDeclinedException>(() => model.Perform(Asked(ScruffPlugin.CleanAction, Path.Combine(_root, "gone.jpg")), CancellationToken.None));
    }

    [Fact]
    public async Task Scruff_cleans_the_file_kibble_moved_where_it_now_is_and_keeps_its_place_in_the_queue()
    {
        // The original goes to the Recycle Bin, which is the Windows shell.
        if (!OperatingSystem.IsWindows())
            return;

        var host = Host("scruff-moved");
        using var model = new ScruffViewModel(host);
        var queued = Path.Combine(_root, "queue", "a.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(queued)!);
        File.WriteAllBytes(queued, TestPictures.WithExif(TestPictures.Jpeg(), orientation: 1, gps: true));
        var stamp = new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(queued, stamp);

        var said = await model.Perform(Asked(ScruffPlugin.CleanAction, Path.Combine(_root, "intake", "a.jpg"), data: new()
        {
            [ActionRequest.DestinationKey] = queued,
        }), CancellationToken.None);

        Assert.Contains("cleaned", said);
        Assert.False(Meows.Media.Metadata.Inspect(File.ReadAllBytes(queued)).CarriesAnything);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(queued));
    }
}
