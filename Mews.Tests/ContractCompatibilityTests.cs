using Mews.Plugins;

namespace Mews.Tests;

public sealed class ContractCompatibilityTests
{
    private static Version Shell => ContractCompatibility.ShellVersion;

    [Fact]
    public void An_assembly_that_uses_no_contract_is_not_refused()
    {
        // It simply has nothing implementing IMewsPlugin, which the type scan handles.
        Assert.Null(ContractCompatibility.Check(null));
    }

    [Fact]
    public void The_shells_own_version_is_accepted()
    {
        Assert.Null(ContractCompatibility.Check(Shell));
    }

    [Fact]
    public void An_older_minor_is_accepted_because_additions_stay_compatible()
    {
        var older = new Version(Shell.Major, Math.Max(Shell.Minor - 1, 0), 0);
        if (older == Shell)
            return; // Nothing older exists within this major yet.

        Assert.Null(ContractCompatibility.Check(older));
    }

    [Fact]
    public void A_plugin_built_against_the_first_contract_still_loads()
    {
        // 0.1.0 is what every plugin written before Category existed was built against, including
        // any living outside this repository. Adding a member with a default must not strand them,
        // and this is the assertion that says so in as many words.
        Assert.Null(ContractCompatibility.Check(new Version(0, 1, 0)));
    }

    [Fact]
    public void A_newer_minor_is_refused_because_it_may_call_members_we_lack()
    {
        var newer = new Version(Shell.Major, Shell.Minor + 1, 0);

        var reason = ContractCompatibility.Check(newer);

        Assert.NotNull(reason);
        Assert.Contains("newer than this shell", reason);
    }

    [Fact]
    public void A_newer_patch_is_refused_too()
    {
        var newer = new Version(Shell.Major, Shell.Minor, Math.Max(Shell.Build, 0) + 1);

        Assert.NotNull(ContractCompatibility.Check(newer));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void Any_major_mismatch_is_refused_in_either_direction(int delta)
    {
        var major = Shell.Major + delta;
        if (major < 0)
            return;

        var reason = ContractCompatibility.Check(new Version(major, Shell.Minor, 0));

        Assert.NotNull(reason);
        Assert.Contains("Major versions must match", reason);
    }

    [Fact]
    public void Versions_are_shown_without_the_trailing_build_number()
    {
        Assert.Equal("1.2.3", ContractCompatibility.Format(new Version(1, 2, 3, 4)));
        Assert.Equal("1.2.0", ContractCompatibility.Format(new Version(1, 2)));
    }

    [Fact]
    public void A_plugin_built_against_the_same_avalonia_is_fine()
    {
        Assert.Null(ContractCompatibility.CheckUi(ContractCompatibility.ShellUiVersion));

        // Drawing nothing at all is not a disagreement about how to draw.
        Assert.Null(ContractCompatibility.CheckUi(null));
    }

    [Fact]
    public void A_plugin_built_against_another_major_of_avalonia_is_refused()
    {
        var ui = ContractCompatibility.ShellUiVersion;

        // The shell and the plugin share one copy of Avalonia on purpose, because a plugin hands
        // back a Control. Two majors mean two unrelated types with that name, and the failure
        // lands somewhere far from the cause.
        var reason = ContractCompatibility.CheckUi(new Version(ui.Major + 1, 0, 0));

        Assert.NotNull(reason);
        Assert.Contains("Avalonia", reason);
    }

    [Fact]
    public void A_plugin_built_against_a_newer_avalonia_is_refused()
    {
        var ui = ContractCompatibility.ShellUiVersion;

        var reason = ContractCompatibility.CheckUi(new Version(ui.Major, ui.Minor + 1, 0));

        Assert.NotNull(reason);
        Assert.Contains("newer", reason);
    }

    [Fact]
    public void An_older_avalonia_within_the_same_major_is_accepted()
    {
        var ui = ContractCompatibility.ShellUiVersion;
        if (ui.Minor == 0 && ui.Build == 0)
            return; // Nothing older exists within this major to test against.

        var older = ui.Build > 0
            ? new Version(ui.Major, ui.Minor, ui.Build - 1)
            : new Version(ui.Major, ui.Minor - 1, 0);

        Assert.Null(ContractCompatibility.CheckUi(older));
    }

    [Fact]
    public void The_shell_accepts_a_plugin_built_the_way_this_repository_builds_them()
    {
        // The real assembly, checked the way discovery checks it. If the guard were too strict
        // it would refuse every plugin here, which is the failure worth catching early.
        var plugin = typeof(Mews.Plugins.Mouser.MouserPlugin).Assembly;

        Assert.Null(ContractCompatibility.CheckAssembly(plugin));
    }
}
