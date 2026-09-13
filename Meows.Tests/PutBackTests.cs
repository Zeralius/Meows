using Meows.Plugins.Abstractions;
using Meows.Plugins.Kibble.ViewModels;
using Meows.Services;
using Meows.ViewModels;

namespace Meows.Tests;

/// <summary>
/// A line in the History tab reversed through the plugin that wrote it. The shell asks first,
/// so the button only appears where pressing it would do something.
/// </summary>
public sealed class PutBackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "putback-" + Guid.NewGuid().ToString("N")[..10]);

    public PutBackTests() => Directory.CreateDirectory(_root);

    private sealed class Reverser(bool can) : IUndoTarget
    {
        public List<StoredEvent> Undone { get; } = [];

        public bool CanUndo(StoredEvent stored) => can && stored.Kind == "sent";

        public string? Undo(StoredEvent stored)
        {
            Undone.Add(stored);
            return stored.Subject.EndsWith("bad") ? "the file is gone" : null;
        }
    }

    [Fact]
    public void The_button_only_shows_for_a_line_its_open_plugin_says_it_can_reverse()
    {
        var store = new MeowsStore(_root, _ => { });
        store.For("meows.kibble").Record("sent", @"C:\x\a.png", "queued");
        store.For("meows.kibble").Record("undone", @"C:\x\a.png", "put back");
        store.For("meows.purrge").Record("recycled", @"C:\x\b.png", "binned");

        var reverser = new Reverser(can: true);
        var fronted = new List<string>();
        var history = new HistoryViewModel(store, id => id, id => id == "meows.kibble" ? reverser : null, fronted.Add);

        history.Selected = history.Lines.Single(l => l.Kind == "sent");
        Assert.True(history.CanPutBack);

        history.Selected = history.Lines.Single(l => l.Kind == "undone");
        Assert.False(history.CanPutBack);

        // Purrge is not open, or does not reverse. Either way, no button.
        history.Selected = history.Lines.Single(l => l.Kind == "recycled");
        Assert.False(history.CanPutBack);

        history.Selected = history.Lines.Single(l => l.Kind == "sent");
        history.PutBackCommand.Execute(null);

        Assert.Equal(["meows.kibble"], fronted);
        Assert.Single(reverser.Undone);
        Assert.Equal("a.png put back.", history.Notice);
    }

    [Fact]
    public void A_refusal_is_shown_as_the_plugin_worded_it()
    {
        var store = new MeowsStore(_root, _ => { });
        store.For("meows.kibble").Record("sent", @"C:\x\bad", "queued");
        var history = new HistoryViewModel(store, id => id, _ => new Reverser(can: true), _ => { });

        history.Selected = history.Lines.Single();
        history.PutBackCommand.Execute(null);

        Assert.Equal("Not put back: the file is gone", history.Notice);
    }

    [Fact]
    public void Kibble_reverses_its_own_line_and_takes_it_off_its_undo_stack()
    {
        using var temp = new TempWorkspace();
        temp.WriteConfig(temp.AddGroup("Alpha"));
        var folder = Path.Combine(temp.Root, "intake");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "a.png"), [1]);
        var host = new FakeHost(Path.Combine(temp.Root, "hostdata"));
        var model = new KibbleViewModel(host);
        model.SetBotRoot(temp.Workspace.Root);
        model.LoadFolder(folder);

        model.SetSelection([model.Incoming[0]]);
        model.SendToCommand.Execute(model.Destinations[0]);
        Assert.Empty(model.Incoming);
        Assert.Equal(1, model.UndoableCount);
        var line = host.Store.Events.Single(e => e.Kind == "sent");

        IUndoTarget target = model;
        Assert.True(target.CanUndo(line));
        Assert.Null(target.Undo(line));

        Assert.Single(model.Incoming);
        Assert.True(File.Exists(Path.Combine(folder, "a.png")));
        Assert.Equal(0, model.UndoableCount);
        Assert.Contains(host.Store.Events, e => e.Kind == "undone");

        // Twice is once: the file is back, so the line is no longer reversible.
        Assert.False(target.CanUndo(line));
        Assert.NotNull(target.Undo(line));
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
