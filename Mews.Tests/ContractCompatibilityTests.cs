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
}
