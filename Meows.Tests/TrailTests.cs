using Meows.Plugins.Trail.Services;

namespace Meows.Tests;

/// <summary>
/// PATH read and judged. Everything here is driven from strings rather than from this machine's
/// registry, because a test that asserts against whatever happens to be installed today passes
/// for the wrong reason and fails for the wrong reason too.
/// </summary>
public sealed class TrailTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "meows-trail-" + Guid.NewGuid().ToString("N")[..8]);

    public TrailTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string Folder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private string Tool(string folder, string file)
    {
        var path = Path.Combine(folder, file);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void An_empty_entry_is_kept_rather_than_dropped()
    {
        // A stray semicolon is a real entry with a real history: older Windows read it as the
        // current folder. Dropping it silently would leave nothing to explain or remove.
        Assert.Equal(["a", "", "b"], PathRead.Split("a;;b"));
        Assert.Empty(PathRead.Split(""));
        Assert.Empty(PathRead.Split(null));
    }

    [Fact]
    public void A_folder_that_is_not_there_is_the_fault_it_looks_like()
    {
        var real = Folder("real");

        var entries = PathRead.From(real + ";" + Path.Combine(_root, "gone"), null, null);

        Assert.Equal(PathFault.None, entries[0].Fault);
        Assert.Equal(PathFault.Missing, entries[1].Fault);
    }

    [Fact]
    public void The_same_folder_twice_is_a_duplicate_however_it_is_spelled()
    {
        // A trailing slash and a different case are the two ways one folder gets on the list
        // twice without looking like it has.
        var real = Folder("Tools");

        var entries = PathRead.From($"{real};{real}\\;{real.ToUpperInvariant()}", null, null);

        Assert.Equal(PathFault.None, entries[0].Fault);
        Assert.Equal(PathFault.Duplicate, entries[1].Fault);
        Assert.Equal(PathFault.Duplicate, entries[2].Fault);
        Assert.Equal("0", entries[1].Note);
    }

    [Fact]
    public void A_variable_nothing_defines_is_called_out_rather_than_called_missing()
    {
        // "%NOPE%\bin does not exist" would be true and useless. The reason it does not exist is
        // that nothing sets NOPE, and that is the thing to say.
        var entries = PathRead.From(@"%MEOWS_NOT_A_REAL_VARIABLE%\bin", null, null);

        Assert.Equal(PathFault.Unexpanded, Assert.Single(entries).Fault);
    }

    [Fact]
    public void A_variable_that_does_expand_is_judged_on_what_it_expands_to()
    {
        var real = Folder("sdk");
        Environment.SetEnvironmentVariable("MEOWS_TEST_SDK", real);
        try
        {
            var entries = PathRead.From(@"%MEOWS_TEST_SDK%", null, null);

            var only = Assert.Single(entries);
            Assert.Equal(PathFault.None, only.Fault);
            Assert.Equal(real, only.Expanded);
            // The raw form is what an edit has to keep: writing the expansion back would freeze
            // it to wherever the variable pointed today.
            Assert.Equal(@"%MEOWS_TEST_SDK%", only.Raw);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MEOWS_TEST_SDK", null);
        }
    }

    [Fact]
    public void Quotes_and_an_empty_entry_are_their_own_faults()
    {
        var entries = PathRead.From($"\"{Folder("quoted")}\";;", null, null);

        Assert.Equal(PathFault.Malformed, entries[0].Fault);
        Assert.Equal(PathFault.Empty, entries[1].Fault);
        Assert.Equal(PathFault.Empty, entries[2].Fault);
    }

    [Fact]
    public void The_scopes_come_in_the_order_windows_searches_them()
    {
        var machine = Folder("m");
        var user = Folder("u");
        var session = Folder("s");

        var entries = PathRead.From(machine, user, $"{machine};{user};{session}");

        Assert.Equal([PathScope.Machine, PathScope.User, PathScope.Process],
            entries.Select(e => e.Scope));
        // Only what the stored halves do not already explain is called a session entry.
        Assert.Equal(session, entries[2].Expanded);
        Assert.Equal([0, 1, 2], entries.Select(e => e.Position));
    }

    [Fact]
    public void Only_the_users_half_can_be_edited()
    {
        var entries = PathRead.From(Path.Combine(_root, "gone-m"), Path.Combine(_root, "gone-u"), null);

        Assert.False(entries[0].CanEdit);
        Assert.True(entries[1].CanEdit);
    }

    [Fact]
    public void The_first_folder_holding_the_command_is_the_one_that_wins()
    {
        var first = Folder("first");
        var second = Folder("second");
        var wins = Tool(first, "dotnet.exe");
        var loses = Tool(second, "dotnet.exe");

        var winner = PathRead.Resolve("dotnet", PathRead.From($"{first};{second}", null, null));

        Assert.NotNull(winner);
        Assert.Equal(wins, winner.FoundAt);
        Assert.Equal(0, winner.Position);
        Assert.Equal([loses], winner.Shadowed);
    }

    [Fact]
    public void A_folder_earlier_on_the_list_wins_even_with_a_later_extension()
    {
        // PATHEXT puts .EXE before .CMD, but folder order comes first: a .cmd in the first
        // folder beats an .exe in the second. Getting this backwards is the whole reason the
        // question is worth asking.
        var first = Folder("first");
        var second = Folder("second");
        var cmd = Tool(first, "tool.cmd");
        Tool(second, "tool.exe");

        var winner = PathRead.Resolve("tool", PathRead.From($"{first};{second}", null, null));

        Assert.NotNull(winner);
        Assert.Equal(cmd, winner.FoundAt);
    }

    [Fact]
    public void A_command_nothing_answers_to_is_no_winner_rather_than_a_wrong_one()
    {
        Assert.Null(PathRead.Resolve("nothing-is-called-this", PathRead.From(Folder("empty"), null, null)));
        Assert.Null(PathRead.Resolve("", PathRead.From(Folder("empty2"), null, null)));
    }

    [Fact]
    public void A_missing_folder_is_not_searched_for_a_command()
    {
        var real = Folder("real");
        var wins = Tool(real, "tool.exe");

        var winner = PathRead.Resolve("tool", PathRead.From($"{Path.Combine(_root, "gone")};{real}", null, null));

        Assert.NotNull(winner);
        Assert.Equal(wins, winner.FoundAt);
    }

    [Fact]
    public void Tidying_takes_the_picked_entries_out_and_leaves_the_rest_as_written()
    {
        var keep = Folder("keep");
        var stored = $"{keep};{Path.Combine(_root, "gone")};%MEOWS_NOT_REAL%\\bin";
        var entries = PathRead.From(null, stored, null);

        var plan = PathWrite.Plan(stored, entries.Where(e => e.IsTrouble));

        Assert.True(plan.ChangesAnything);
        Assert.Equal(keep, plan.After);
        Assert.Equal(2, plan.Removed.Count);
        // The raw form survives, so a variable is still a variable on whatever is kept.
        Assert.DoesNotContain("%", plan.After);
    }

    [Fact]
    public void Tidying_never_touches_the_machines_half()
    {
        var gone = Path.Combine(_root, "gone");
        var entries = PathRead.From(gone, gone, null);

        // Both entries are faulty and both are picked; only the user's may go, and the plan is
        // made against the user's stored string, which knows nothing of the machine's.
        var plan = PathWrite.Plan(gone, entries);

        Assert.Equal("", plan.After);
        Assert.Single(plan.Removed);
    }

    [Fact]
    public void One_of_two_identical_entries_goes_and_the_other_stays()
    {
        var keep = Folder("keep");
        var stored = $"{keep};{keep}";
        var entries = PathRead.From(null, stored, null);

        // The second is the duplicate. Removing it by value, without care, would remove both and
        // take the working entry with it.
        var plan = PathWrite.Plan(stored, entries.Where(e => e.Fault == PathFault.Duplicate));

        Assert.Equal(keep, plan.After);
        Assert.Single(plan.Removed);
    }

    [Fact]
    public void A_plan_against_a_path_that_has_since_changed_changes_nothing()
    {
        var gone = Path.Combine(_root, "gone");
        var entries = PathRead.From(null, gone, null);

        // Read, then somebody edited PATH elsewhere. The entry that was ticked is not in the
        // stored string any more, so nothing matches and nothing is written.
        var plan = PathWrite.Plan(@"C:\somewhere\else", entries);

        Assert.False(plan.ChangesAnything);
        Assert.Empty(plan.Removed);
    }

    [Fact]
    public void The_old_value_is_written_down_before_anything_would_change()
    {
        var before = @"C:\one;C:\two";

        var file = PathWrite.Backup(Path.Combine(_root, "data"), before);

        Assert.True(File.Exists(file));
        Assert.Equal(before, File.ReadAllText(file));
    }
}
