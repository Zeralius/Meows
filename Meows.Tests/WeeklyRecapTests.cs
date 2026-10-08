using Meows.Plugins.Abstractions;
using Meows.Services;

namespace Meows.Tests;

/// <summary>
/// The weekly recap: a week of the history counted, the Recycle Bin and rules read off what the
/// lines already carry, each plugin's doings in its own words, and never due in the first week.
/// </summary>
public sealed class WeeklyRecapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "meows-recap-" + Guid.NewGuid().ToString("N")[..8]);

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

    private static StoredEvent Line(string plugin, string kind, Dictionary<string, string>? data = null) =>
        new(0, DateTime.UtcNow, plugin, kind, "x", null, data ?? []);

    [Fact]
    public void A_week_is_counted_from_what_the_lines_carry()
    {
        StoredEvent[] week =
        [
            Line("meows.purrge", "recycled", new() { ["size"] = "1048576" }),
            Line("meows.purrge", "recycled", new() { ["size"] = "1048576" }),
            Line("meows.chonk", "recycled", new() { ["size"] = "not a number" }),
            Line("meows.carry", "carried", new() { ["bytes"] = "2147483648" }),
            Line("meows.portion", "checked", new() { ["instinct.rule"] = "r1" }),
            Line("meows.weighin", "reading"),
        ];

        var recap = WeeklyRecap.Of(week, DateTime.UtcNow.AddDays(-7), DateTime.UtcNow);

        Assert.Equal(6, recap.Things);
        Assert.Equal(2 * 1048576, recap.Recycled);
        Assert.Equal(2147483648, recap.Carried);
        Assert.Equal(1, recap.RulesFired);
        Assert.Equal(("meows.purrge", "recycled", 2), recap.ByKind[0]);

        var lines = WeeklyRecap.Lines(recap, id => id.Split('.')[^1],
            (plugin, kind) => plugin == "meows.purrge" && kind == "recycled" ? "sent a copy to the Recycle Bin" : null, MeowsText.Current);
        Assert.Equal("6 things done", lines[0]);
        Assert.Contains("2 MB sent to the Recycle Bin", lines);
        Assert.Contains("2 GB carried to another drive", lines);
        Assert.Contains("1 set off by rules", lines);
        Assert.Contains("purrge · sent a copy to the Recycle Bin × 2", lines);
        Assert.Contains("weighin · reading × 1", lines);
        Assert.Equal("6 things done; 2 MB sent to the Recycle Bin; 1 set off by rules", WeeklyRecap.Summary(recap, MeowsText.Current));
    }

    [Fact]
    public void A_quiet_week_says_so_and_the_first_week_is_never_due()
    {
        var quiet = WeeklyRecap.Of([], DateTime.UtcNow.AddDays(-7), DateTime.UtcNow);
        Assert.Equal(["Nothing was recorded this week."], WeeklyRecap.Lines(quiet, id => id, (_, _) => null, MeowsText.Current));

        var now = DateTime.UtcNow;
        Assert.False(WeeklyRecap.IsDue(null, now));
        Assert.False(WeeklyRecap.IsDue(now.AddDays(-6), now));
        Assert.True(WeeklyRecap.IsDue(now.AddDays(-7), now));
    }

    [Fact]
    public void The_store_hands_back_one_week_and_no_more()
    {
        var store = new MeowsStore(_root, _ => { });
        store.For("meows.test").Record("done", "a");
        store.For("meows.test").Record("done", "b");

        var now = DateTime.UtcNow.AddSeconds(1);
        Assert.Equal(2, store.Between(now.AddDays(-7), now).Count);
        Assert.Empty(store.Between(now.AddDays(-14), now.AddDays(-7)));
        Assert.Equal("1.5 GB", WeeklyRecap.Humanise(1610612736));
    }
}
