using Meows.Plugins.Kibble.Services;
using Meows.Plugins.Kibble.ViewModels;

namespace Meows.Tests;

/// <summary>
/// Undo reaching past the last action: back through this session's sends one batch at a time,
/// and after a restart, back through what the journal says is still sitting in a queue.
/// </summary>
public sealed class KibbleUndoHistoryTests
{
    private static (KibbleViewModel Model, FakeHost Host, TempWorkspace Temp, string Folder) Open(params string[] names)
    {
        var temp = new TempWorkspace();
        temp.WriteConfig(temp.AddGroup("Alpha"), temp.AddGroup("Beta", "-100222"));

        var folder = Path.Combine(temp.Root, "intake");
        Directory.CreateDirectory(folder);
        foreach (var name in names)
            File.WriteAllBytes(Path.Combine(folder, name), System.Text.Encoding.UTF8.GetBytes(name));

        var host = new FakeHost(Path.Combine(temp.Root, "hostdata"));
        var model = new KibbleViewModel(host);
        model.SetBotRoot(temp.Workspace.Root);
        model.LoadFolder(folder);
        return (model, host, temp, folder);
    }

    /// <summary>The same host and settings, opened again, as a restart would.</summary>
    private static KibbleViewModel Reopen(FakeHost host, string folder)
    {
        var model = new KibbleViewModel(host);
        model.LoadFolder(folder);
        return model;
    }

    private static void Send(KibbleViewModel model, string name, string group)
    {
        model.SetSelection([model.Incoming.First(f => f.FileName == name)]);
        model.SendToCommand.Execute(model.Destinations.First(d => d.Name == group));
    }

    [Fact]
    public void Undo_walks_back_one_send_at_a_time()
    {
        var (model, _, temp, folder) = Open("a.png", "b.png", "c.png");
        using var _t = temp;

        Send(model, "a.png", "Alpha");
        Send(model, "b.png", "Beta");
        Assert.Equal(2, model.UndoableCount);
        Assert.Equal("Undo · 2", model.UndoLabel);
        Assert.Contains("b.png", model.UndoHint);
        Assert.Contains("Beta", model.UndoHint);

        model.UndoCommand.Execute(null);
        Assert.True(File.Exists(Path.Combine(folder, "b.png")));
        Assert.False(File.Exists(Path.Combine(folder, "a.png")));
        Assert.Equal(1, model.UndoableCount);
        Assert.Equal("Undo", model.UndoLabel);

        model.UndoCommand.Execute(null);
        Assert.True(File.Exists(Path.Combine(folder, "a.png")));
        Assert.Equal(0, model.UndoableCount);
        Assert.False(model.UndoCommand.CanExecute(null));
        Assert.Equal(3, model.Incoming.Count);
    }

    [Fact]
    public void A_send_from_before_the_restart_can_still_be_undone()
    {
        var (model, host, temp, folder) = Open("a.png", "b.png");
        using var _t = temp;

        Send(model, "a.png", "Alpha");
        var queued = Path.Combine(temp.Workspace.ToSendFolder(model.Destinations.First(d => d.Name == "Alpha").Group), "a.png");
        Assert.True(File.Exists(queued));

        var again = Reopen(host, folder);

        // Seeded from the journal, not from anything the old view model still held.
        Assert.Equal(1, again.UndoableCount);
        Assert.Contains("a.png", again.UndoHint);

        again.UndoCommand.Execute(null);

        Assert.True(File.Exists(Path.Combine(folder, "a.png")));
        Assert.False(File.Exists(queued));
        Assert.Equal(2, again.Incoming.Count);
        Assert.Contains(host.Store.Events, e => e.Kind == "undone" && e.Subject.EndsWith("a.png"));
    }

    [Fact]
    public void A_file_the_bot_has_already_posted_is_not_offered()
    {
        var (model, host, temp, folder) = Open("a.png", "b.png");
        using var _t = temp;

        Send(model, "a.png", "Alpha");
        Send(model, "b.png", "Alpha");

        // The bot posting a file moves it out of the queue. Nothing to put back after that.
        var alpha = model.Destinations.First(d => d.Name == "Alpha").Group;
        File.Delete(Path.Combine(temp.Workspace.ToSendFolder(alpha), "a.png"));

        var again = Reopen(host, folder);

        Assert.Equal(1, again.UndoableCount);
        Assert.Contains("b.png", again.UndoHint);
    }

    [Fact]
    public void A_comic_survives_the_journal_and_unpacks_after_a_restart()
    {
        var (model, host, temp, folder) = Open("p1.png", "p2.png", "p3.png");
        using var _t = temp;

        model.SetSelection([.. model.Incoming]);
        model.SendToCommand.Execute(model.Destinations.First(d => d.Name == "Alpha"));
        Assert.Empty(model.Incoming);

        var line = Assert.Single(host.Store.Events);
        Assert.True(line.Data.ContainsKey("pages"));
        Assert.True(line.Data.ContainsKey("batch"));

        var again = Reopen(host, folder);
        Assert.Equal(1, again.UndoableCount);
        // One comic went, so the hint names the comic rather than counting its pages.
        Assert.Contains(".cbz", again.UndoHint);

        again.UndoCommand.Execute(null);

        Assert.Equal(3, again.Incoming.Count);
        Assert.True(File.Exists(Path.Combine(folder, "p2.png")));
        Assert.Empty(temp.Workspace.Scan(temp.Workspace.ToSendFolder(model.Destinations.First(d => d.Name == "Alpha").Group)));
    }

    [Fact]
    public void Files_sent_together_come_back_together()
    {
        var (model, host, temp, folder) = Open("a.png", "b.png", "c.png");
        using var _t = temp;

        model.ChooseFilesCommand.Execute(null);
        model.SetSelection([model.Incoming[0], model.Incoming[1]]);
        model.SendToCommand.Execute(model.Destinations.First(d => d.Name == "Alpha"));
        Assert.Single(model.Incoming);

        var again = Reopen(host, folder);

        // Two journal lines, one batch id, one step back.
        Assert.Equal(1, again.UndoableCount);
        again.UndoCommand.Execute(null);
        Assert.Equal(3, again.Incoming.Count);
    }

    [Fact]
    public void A_send_from_another_folder_goes_back_there_and_not_into_the_grid()
    {
        var (model, host, temp, folder) = Open("a.png");
        using var _t = temp;

        Send(model, "a.png", "Alpha");

        var other = Path.Combine(temp.Root, "elsewhere");
        Directory.CreateDirectory(other);
        File.WriteAllBytes(Path.Combine(other, "z.png"), [1]);

        var again = Reopen(host, other);
        Assert.Single(again.Incoming);

        again.UndoCommand.Execute(null);

        Assert.True(File.Exists(Path.Combine(folder, "a.png")));
        Assert.Single(again.Incoming);
        Assert.Contains("not the folder open here", again.StatusMessage);
    }

    [Fact]
    public void A_journal_line_turns_back_into_a_result()
    {
        var pages = new List<BundledPage>
        {
            new(@"C:\x\p1.png", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)),
            new(@"C:\x\p2.png", new DateTime(2026, 1, 2, 3, 4, 6, DateTimeKind.Utc)),
        };

        var json = Intake.PagesToJournal(pages);
        var result = Intake.FromJournal("sent", @"C:\x\p1.png", new Dictionary<string, string>
        {
            ["destination"] = @"C:\q\comic.cbz",
            ["pages"] = json,
        });

        Assert.NotNull(result);
        Assert.Equal(IntakeOutcome.Sent, result!.Outcome);
        Assert.Equal(2, result.Bundled!.Count);
        Assert.Equal(pages[1].Modified, result.Bundled[1].Modified);

        Assert.Null(Intake.FromJournal("posted", "x", new Dictionary<string, string>()));
        Assert.Null(Intake.FromJournal("sent", "x", new Dictionary<string, string>()));
    }
}
