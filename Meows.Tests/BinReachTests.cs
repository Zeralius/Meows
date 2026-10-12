using Meows.Disk;

namespace Meows.Tests;

/// <summary>
/// The cases where Windows would delete for good rather than recycle, which RecycleBin.Send
/// has to hold back instead of handing to the shell.
/// </summary>
public sealed class BinReachTests
{
    [Fact]
    public void A_network_share_has_no_bin()
    {
        Assert.Equal(BinRefusal.Network, BinReach.Check(@"\\server\share\photos\a.jpg"));
    }

    [Fact]
    public void A_path_too_long_for_the_shell_is_refused_rather_than_stopping_the_batch()
    {
        var deep = Path.Combine(Path.GetTempPath(), new string('a', 120), new string('b', 120), "c.txt");

        Assert.Equal(BinRefusal.TooLong, BinReach.Check(deep));
    }

    [Fact]
    public void An_ordinary_file_in_temp_can_go_to_the_bin()
    {
        var file = Path.Combine(Path.GetTempPath(), $"binreach-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "x");
        try
        {
            Assert.Equal(BinRefusal.None, BinReach.Check(file));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
