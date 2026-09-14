using Meows.Services;

namespace Meows.Tests;

/// <summary>The shell's side of a pause: the schedule stays, the passes stop, and Resume runs one at once.</summary>
public sealed class PauseTests
{
    [Fact]
    public async Task A_paused_schedule_runs_no_passes_and_resume_runs_one_at_once()
    {
        using var background = new BackgroundTaskService(new NotificationCenter(), new ShellLog());
        var passes = 0;
        var task = background.ScheduleForShell("Ticking", TimeSpan.FromMilliseconds(40), _ =>
        {
            Interlocked.Increment(ref passes);
            return Task.CompletedTask;
        }, runImmediately: true);

        await WaitUntil(() => Volatile.Read(ref passes) >= 2);
        var id = background.Watches().Single(w => w.Title == "Ticking").Id;
        Assert.NotEmpty(id);

        Assert.True(background.PauseWatch(id, DateTime.MaxValue));
        await Task.Delay(120);
        var whilePaused = Volatile.Read(ref passes);
        await Task.Delay(200);
        Assert.Equal(whilePaused, Volatile.Read(ref passes));
        var watch = background.Watches().Single(w => w.Id == id);
        Assert.True(watch.IsPaused);
        Assert.Null(watch.NextDueAt);

        Assert.True(background.ResumeWatch(id));
        await WaitUntil(() => Volatile.Read(ref passes) > whilePaused);
        Assert.False(background.Watches().Single(w => w.Id == id).IsPaused);

        // A timed pause ends on its own.
        Assert.True(background.PauseWatch(id, DateTime.Now.AddMilliseconds(150)));
        var before = Volatile.Read(ref passes);
        await WaitUntil(() => Volatile.Read(ref passes) > before + 1);

        task.Cancel();
        Assert.False(background.PauseWatch("no such watch", DateTime.MaxValue));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(50);
        Assert.True(condition(), "timed out waiting");
    }
}
