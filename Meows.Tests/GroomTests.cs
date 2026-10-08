using Meows.Plugins.Purrge.Services;
using Meows.Plugins.Purrge.ViewModels;

namespace Meows.Tests;

/// <summary>
/// Groom: every new name is in the preview before anything moves, a clash holds the whole run
/// back, a swap goes through, the extension is never touched, and the last run can be undone.
/// </summary>
public sealed class GroomTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "groom-" + Guid.NewGuid().ToString("N")[..10]);

    public GroomTests() => Directory.CreateDirectory(_root);

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

    private void Touch(string name, string content = "x") => File.WriteAllText(Path.Combine(_root, name), content);

    private string[] Here() => Directory.GetFiles(_root).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void The_steps_run_in_order_and_the_extension_stays()
    {
        Touch("Holiday (1).JPG");
        Touch("holiday_beach - Copy.jpg");
        Touch("notes.txt");

        var rows = Groom.Plan(_root, new GroomRule
        {
            Filter = "*.jpg",
            DropCopyCounter = true,
            Find = "_",
            Replace = " ",
            Case = GroomCase.Title,
            Template = "{n} {name}",
            Start = 7,
            Digits = 2,
        });

        Assert.Equal(
            [("Holiday (1).JPG", "07 Holiday.JPG"), ("holiday_beach - Copy.jpg", "08 Holiday Beach.jpg")],
            rows.Select(r => (r.From, r.To)));
        Assert.All(rows, r => Assert.Equal(GroomProblem.None, r.Problem));
    }

    [Fact]
    public void A_clash_is_in_the_preview_and_holds_the_whole_run_back()
    {
        Touch("a (1).png");
        Touch("a.png", "the original");
        Touch("b (1).png");
        Touch("c (1).png");
        Touch("c (2).png");

        var rows = Groom.Plan(_root, new GroomRule { DropCopyCounter = true });

        Assert.Equal(GroomProblem.Taken, rows.Single(r => r.From == "a (1).png").Problem);
        Assert.Equal(GroomProblem.Twice, rows.Single(r => r.From == "c (1).png").Problem);
        Assert.Equal(GroomProblem.None, rows.Single(r => r.From == "b (1).png").Problem);

        var (outcome, run) = Groom.Apply(rows);
        Assert.Equal(0, outcome.Moved);
        Assert.Null(run);
        Assert.Contains("b (1).png", Here());
        Assert.Equal(GroomProblem.BadPattern, Groom.Plan(_root, new GroomRule { Find = "(", UseRegex = true })[0].Problem);
        Assert.Equal(GroomProblem.Invalid, Groom.Plan(_root, new GroomRule { Filter = "b*", Template = "what?" })[0].Problem);
    }

    [Fact]
    public void Two_names_swap_and_the_run_is_undone()
    {
        Touch("one.txt", "1");
        Touch("two.txt", "2");

        // A swap: each name is the other's, and neither file is in the way because both are moving.
        var swapped = new List<GroomRow> { new(_root, "one.txt", "two.txt", GroomProblem.None), new(_root, "two.txt", "one.txt", GroomProblem.None) };
        var (outcome, run) = Groom.Apply(swapped);
        Assert.Equal(2, outcome.Moved);
        Assert.Equal("2", File.ReadAllText(Path.Combine(_root, "one.txt")));

        var back = Groom.Undo(run!);
        Assert.Equal(2, back.Moved);
        Assert.Empty(back.Failed);
        Assert.Equal("1", File.ReadAllText(Path.Combine(_root, "one.txt")));
    }

    [Fact]
    public void The_tab_renames_records_and_puts_the_names_back_after_a_restart()
    {
        Touch("IMG_0001.jpg");
        Touch("IMG_0002.jpg");
        var host = new FakeHost(Path.Combine(Path.GetTempPath(), "groom-host-" + Guid.NewGuid().ToString("N")[..8]));

        using (var purrge = new PurrgeViewModel(host))
        {
            purrge.IsGroomMode = true;
            Assert.False(purrge.IsDuplicatesMode);
            purrge.Groom.Folder = _root;
            purrge.Groom.Template = "Cat {n}";
            Assert.Equal(2, purrge.Groom.ChangeCount);
            Assert.True(purrge.Groom.RunCommand.CanExecute(null));

            purrge.Groom.RunCommand.Execute(null);
            Assert.Equal(["Cat 001.jpg", "Cat 002.jpg"], Here());
            Assert.Contains(host.Store.Recent(10), r => r.Kind == "renamed");
        }

        using var again = new PurrgeViewModel(host);
        Assert.True(again.IsGroomMode);
        Assert.Equal("Cat {n}", again.Groom.Template);
        Assert.True(again.Groom.CanUndo);
        again.Groom.UndoCommand.Execute(null);
        Assert.Equal(["IMG_0001.jpg", "IMG_0002.jpg"], Here());
        Assert.False(again.Groom.CanUndo);
    }
}
