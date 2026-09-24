using Meows.Plugins.Abstractions;
using Meows.Services;

namespace Meows.Tests;

/// <summary>
/// Plugin cost: how long a plugin took to open and what its background work added up to, timed
/// around its own code, said in one line on its card, and slow opening marked.
/// </summary>
public sealed class PluginCostTests
{
    [Fact]
    public void Runs_add_up_per_plugin_and_the_shell_is_not_counted()
    {
        var costs = new PluginCosts();
        costs.Ran("meows.weighin", TimeSpan.FromSeconds(3), failed: false);
        costs.Ran("meows.weighin", TimeSpan.FromSeconds(70), failed: true);
        costs.Ran("", TimeSpan.FromHours(1), failed: false);
        costs.Opened("meows.weighin", TimeSpan.FromMilliseconds(120));

        var cost = costs.For("MEOWS.WEIGHIN");
        Assert.Equal(2, cost.Runs);
        Assert.Equal(TimeSpan.FromSeconds(73), cost.Busy);
        Assert.Equal(TimeSpan.FromSeconds(70), cost.Longest);
        Assert.Equal(1, cost.Failures);
        Assert.False(cost.SlowToOpen);
        Assert.Equal(PluginCost.Nothing, costs.For("meows.other"));

        Assert.Equal("opened in 120 ms · 2 background runs, 1 min 13 s busy, the longest 1 min 10 s · 1 failed",
            PluginCosts.Describe(cost, MeowsText.Current));
        Assert.Equal("", PluginCosts.Describe(PluginCost.Nothing, MeowsText.Current));
    }

    [Fact]
    public void Slow_opening_is_marked()
    {
        var costs = new PluginCosts();
        costs.Opened("meows.chonk", TimeSpan.FromSeconds(1.5));

        Assert.True(costs.For("meows.chonk").SlowToOpen);
        Assert.StartsWith("slow to open: 1.5 s", PluginCosts.Describe(costs.For("meows.chonk"), MeowsText.Current));
    }

    [Fact]
    public async Task Background_work_is_timed_around_the_plugins_own_code()
    {
        var service = new BackgroundTaskService(new NotificationCenter(), new ShellLog(Path.Combine(Path.GetTempPath(), "cost-" + Guid.NewGuid().ToString("N")[..8] + ".log")))
        {
            Costs = new PluginCosts(),
        };
        var work = new PluginBackgroundWork(service, "meows.test", "Test");
        var done = new TaskCompletionSource();

        work.Run("sleepy", async _ =>
        {
            await Task.Delay(60);
            done.SetResult();
        });
        await done.Task;
        for (var i = 0; i < 50 && service.Costs.For("meows.test").Runs == 0; i++)
            await Task.Delay(10);

        var cost = service.Costs.For("meows.test");
        Assert.Equal(1, cost.Runs);
        Assert.True(cost.Busy >= TimeSpan.FromMilliseconds(50), $"only {cost.Busy}");
        service.Dispose();
    }
}
