using Meows.Plugins;

namespace Meows.Tests;

public sealed class ContractCompatibilityTests
{
    private static Version Shell => ContractCompatibility.ShellVersion;

    [Fact]
    public void An_assembly_that_uses_no_contract_is_not_refused()
    {
        // It simply has nothing implementing IMeowsPlugin, which the type scan handles.
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
    public void The_first_contract_of_this_major_still_loads()
    {
        // Every member added since the major began has a default, so a plugin built against
        // the first release of this major must not be stranded by any of them. Through 0.x that
        // was 0.1.0; from 1.0.0 the promise starts again, and this is the assertion that says so.
        Assert.Null(ContractCompatibility.Check(new Version(Shell.Major, 0, 0)));
    }

    [Fact]
    public void A_plugin_from_before_1_0_0_is_refused_with_the_major_reason()
    {
        // 0.x plugins were built against a contract that 1.0.0 is allowed to have changed, so
        // they are refused before any of their code runs, and told to rebuild.
        if (Shell.Major == 0)
            return;

        var reason = ContractCompatibility.Check(new Version(0, 10, 0));

        Assert.NotNull(reason);
        Assert.Contains("0.10.0", reason);
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
        var plugin = typeof(Meows.Plugins.Mouser.MouserPlugin).Assembly;

        Assert.Null(ContractCompatibility.CheckAssembly(plugin));
    }
}

/// <summary>
/// The template is what a stranger starts from, and it pins the contract it compiles against.
/// An older pin still loads, but it hides every member added since, and nobody bumps a pin in a
/// file they never open. So the build says when it has fallen behind.
/// </summary>
public sealed class TemplatePinTests
{
    [Fact]
    public void The_template_pins_the_contract_version_the_repository_ships()
    {
        var here = new DirectoryInfo(AppContext.BaseDirectory);
        while (here is not null && !File.Exists(Path.Combine(here.FullName, "Meows.sln")))
            here = here.Parent;
        Assert.NotNull(here);

        var contract = File.ReadAllText(Path.Combine(here.FullName, "Meows.Plugins.Abstractions", "Meows.Plugins.Abstractions.csproj"));
        var template = File.ReadAllText(Path.Combine(here.FullName, "template", "MyPlugin", "MyPlugin.csproj"));

        var shipped = System.Text.RegularExpressions.Regex.Match(contract, @"<ContractVersion>([^<]+)</ContractVersion>").Groups[1].Value;
        var pinned = System.Text.RegularExpressions.Regex.Match(template, @"Include=""Meows\.Plugins\.Abstractions"" Version=""([^""]+)""").Groups[1].Value;

        Assert.Equal(shipped, pinned);
    }
}
