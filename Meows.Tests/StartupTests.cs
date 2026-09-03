using Meows.Services;

namespace Meows.Tests;

/// <summary>
/// Starting Meows at login.
///
/// The registry itself is not touched here: these tests run on a real machine and a startup
/// entry is not theirs to add. What is checked is the reading, which is where the answers can
/// be wrong quietly, and it is worth being right about because the whole point is that the tick
/// on the tab says what Windows will really do.
/// </summary>
public class StartupTests
{
    /// <summary>
    /// Windows keeps a separate record of whether somebody switched a startup entry off, and
    /// leaves the Run value exactly where it was. Read only the Run value and a program that
    /// has not started in months still reports as starting.
    ///
    /// Bit zero of the first byte carries it. Two and six are enabled, three and seven are
    /// disabled, which is that rule written out.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 2, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 6, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 3, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 7, 0, 0, 0 }, true)]
    public void Windows_can_switch_the_entry_off_without_removing_it(byte[] approval, bool blocked)
    {
        Assert.Equal(blocked, StartWithWindows.IsBlocked(approval));
    }

    [Fact]
    public void No_record_at_all_counts_as_allowed()
    {
        // An entry nobody has touched has no approval value. That is the normal case.
        Assert.False(StartWithWindows.IsBlocked(null));
        Assert.False(StartWithWindows.IsBlocked([]));
    }

    [Fact]
    public void A_registered_command_line_is_matched_through_its_quotes()
    {
        // Stored as a command line, not a path, and quoted because Program Files has a space
        // in it. Comparing the raw strings would call every registered copy a different one.
        var exe = Path.Combine(Path.GetTempPath(), "Meows", "Meows.exe");

        Assert.True(StartWithWindows.PointsAt($"\"{exe}\"", exe));
        Assert.True(StartWithWindows.PointsAt(exe, exe));
        Assert.True(StartWithWindows.PointsAt($"  \"{exe}\"  ", exe));
    }

    [Fact]
    public void Case_and_shape_do_not_make_it_a_different_copy()
    {
        var exe = Path.Combine(Path.GetTempPath(), "Meows", "Meows.exe");
        var awkward = Path.Combine(Path.GetTempPath(), "Meows", ".", "Meows.exe").ToUpperInvariant();

        Assert.True(StartWithWindows.PointsAt(awkward, exe));
    }

    [Fact]
    public void Another_copy_is_reported_as_another_copy()
    {
        // The folder gets moved, and the entry keeps launching whatever is still at the old
        // path. Saying "on" there would be true and useless.
        var here = Path.Combine(Path.GetTempPath(), "Meows", "Meows.exe");
        var there = Path.Combine(Path.GetTempPath(), "Somewhere else", "Meows.exe");

        Assert.False(StartWithWindows.PointsAt(there, here));
    }

    [Fact]
    public void Nothing_registered_points_at_nothing()
    {
        Assert.False(StartWithWindows.PointsAt(null, @"C:\Meows\Meows.exe"));
        Assert.False(StartWithWindows.PointsAt("\"" + @"C:\Meows\Meows.exe" + "\"", null));
    }

    [Fact]
    public void A_value_that_is_not_a_path_is_not_this_one()
    {
        // Whatever else it is, it is not us, and working that out must not throw from inside
        // a property the settings tab reads on every repaint.
        Assert.False(StartWithWindows.PointsAt("|not< a >path", @"C:\Meows\Meows.exe"));
    }

    [Fact]
    public void Reading_the_real_startup_list_answers_rather_than_throwing()
    {
        // Read-only, and it has to cope with the entry being absent, which on this machine it
        // is. The tab calls this on every repaint, so an exception here would be a dead tab.
        var registration = StartWithWindows.Read();

        Assert.True(Enum.IsDefined(registration.State));
    }

    [Fact]
    public void The_test_runner_is_not_mistaken_for_the_application()
    {
        // testhost.exe is every bit as much an .exe as Meows.exe, and an earlier version of
        // this happily offered to start it at login. What is running has to be named after the
        // assembly asking, not merely end in .exe.
        Assert.Null(StartWithWindows.ExecutablePath);
    }
}
