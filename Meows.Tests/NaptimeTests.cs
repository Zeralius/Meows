using Meows.Plugins.Abstractions;
using Meows.Plugins.Naptime;
using Meows.Plugins.Naptime.Services;
using Meows.Plugins.Naptime.ViewModels;

namespace Meows.Tests;

/// <summary>
/// The arithmetic behind the habits: weeks starting Monday, streaks with one miss forgiven,
/// and the header. Every one of these is told what today is rather than reading the clock.
/// </summary>
public class StreaksTests
{
    private static readonly DateTime Today = new(2026, 10, 5);

    private static IMeowsText Text() => TestStrings.Load();

    private static Habit Ticked(params int[] daysAgo) => new()
    {
        Name = "Gym",
        Target = 3,
        DoneDays = daysAgo.Select(d => Today.AddDays(-d)).ToList(),
    };

    [Fact]
    public void Weeks_start_monday()
    {
        // 2026-10-05 is a Monday.
        Assert.Equal(new DateTime(2026, 10, 5), Streaks.WeekStart(Today));
        Assert.Equal(new DateTime(2026, 10, 5), Streaks.WeekStart(Today.AddDays(6)));
        Assert.Equal(new DateTime(2026, 10, 12), Streaks.WeekStart(Today.AddDays(7)));
    }

    [Fact]
    public void This_week_counts_monday_to_today_and_never_the_future()
    {
        var habit = Ticked(0, -1, -8);

        Assert.Equal(1, Streaks.ThisWeek(habit, Today));
    }

    [Fact]
    public void A_streak_counts_back_with_one_miss_bridged()
    {
        Assert.Equal(0, Streaks.Of(Ticked(), Today));
        Assert.Equal(1, Streaks.Of(Ticked(0), Today));
        Assert.Equal(3, Streaks.Of(Ticked(0, 1, 2), Today));
        // Today still unticked is a day in progress when yesterday has one.
        Assert.Equal(2, Streaks.Of(Ticked(1, 2), Today));
        // One miss is bridged to the tick before it and still counts: Monday to Thursday.
        Assert.Equal(5, Streaks.Of(Ticked(0, 1, 3, 4), Today));
        // Two in a row break the run, whatever came before.
        Assert.Equal(1, Streaks.Of(Ticked(0, 3, 4), Today));
        // Nothing since the day before yesterday is no streak.
        Assert.Equal(0, Streaks.Of(Ticked(2, 3), Today));
    }

    [Fact]
    public void Targets_are_met_by_count_not_by_streak()
    {
        // Today is a Monday, so only today's tick sits in this week.
        Assert.True(Streaks.MetTarget(new Habit { Target = 1, DoneDays = [Today] }, Today));
        Assert.False(Streaks.MetTarget(Ticked(0), Today));
    }

    [Fact]
    public void The_header_counts_ticks_today_and_weeks_met()
    {
        var text = Text();

        Assert.Equal("", Streaks.SummaryOf([], Today, text));
        Assert.Null(Streaks.GlanceOf([], Today, text));

        // Today is a Monday: only today's ticks sit in this week.
        var habits = new List<Habit>
        {
            new() { Name = "Gym", Target = 1, DoneDays = [Today] },
            new() { Name = "Read", Target = 3, DoneDays = [Today.AddDays(-5)] },
        };
        Assert.Equal("1 of 2 today, 1 weeks met", Streaks.SummaryOf(habits, Today, text));
        Assert.False(Streaks.GlanceOf(habits, Today, text)?.IsTrouble);
    }

    [Fact]
    public void Two_quiet_days_are_the_nudge()
    {
        var text = Text();

        Assert.True(Streaks.GlanceOf([Ticked(2, 3)], Today, text)?.IsTrouble);
        Assert.False(Streaks.GlanceOf([Ticked(1)], Today, text)?.IsTrouble);
        Assert.False(Streaks.GlanceOf([Ticked(0)], Today, text)?.IsTrouble);
    }
}

/// <summary>
/// The tab itself: habits added, ticked and retargeted, quiet days said out loud, and the half
/// that matters with no window.
/// </summary>
public sealed class NaptimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "naptime-" + Guid.NewGuid().ToString("N")[..10]);

    public NaptimeTests()
    {
        Directory.CreateDirectory(_root);
        TestStrings.Install();
    }

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

    private FakeHost Host(string name) => new(Path.Combine(_root, "host-" + name));

    [Fact]
    public void Opens_with_something_to_say()
    {
        using var model = new NaptimeViewModel(Host("open"));

        Assert.False(string.IsNullOrWhiteSpace(model.Status));
        Assert.False(model.HasError);
    }

    [Fact]
    public void Refresh_reports_when_it_ran()
    {
        using var model = new NaptimeViewModel(Host("refresh"));

        model.RefreshCommand.Execute(null);

        Assert.Contains(":", model.Status);
    }

    [Fact]
    public void Habits_are_added_once_ticked_and_journaled()
    {
        var host = Host("habits");
        using var model = new NaptimeViewModel(host);

        model.NewHabitName = "Gym";
        model.AddCommand.Execute(null);
        model.NewHabitName = "gym";
        model.AddCommand.Execute(null);

        var habit = Assert.Single(model.Habits);
        Assert.Contains("already", model.Status);

        model.Selected = habit;
        habit.DoneToday = true;

        Assert.Contains("1 of 1 today", model.Summary);
        Assert.Contains(host.Store.Events, e => e.Kind == "ticked");

        habit.DoneToday = false;
        Assert.Contains("0 of 1 today", model.Summary);
    }

    [Fact]
    public void Targets_move_between_once_and_seven_times_a_week()
    {
        using var model = new NaptimeViewModel(Host("target"));
        model.NewHabitName = "Read";
        model.AddCommand.Execute(null);
        var habit = model.Habits[0];

        habit.TargetUpCommand.Execute(null);
        habit.TargetUpCommand.Execute(null);
        Assert.Equal(5, habit.Target);

        for (var i = 0; i < 10; i++)
            habit.TargetUpCommand.Execute(null);
        Assert.Equal(7, habit.Target);

        for (var i = 0; i < 10; i++)
            habit.TargetDownCommand.Execute(null);
        Assert.Equal(1, habit.Target);
    }

    [Fact]
    public void Deleting_forgets_the_habit_never_the_days()
    {
        var host = Host("delete");
        using var model = new NaptimeViewModel(host);

        model.NewHabitName = "Gym";
        model.AddCommand.Execute(null);
        model.Habits[0].DeleteCommand.Execute(null);

        Assert.Empty(model.Habits);
        Assert.Contains("deleted", model.Status);
    }

    [Fact]
    public void Two_quiet_days_are_raised_and_a_tick_takes_them_down()
    {
        var host = Host("idle");
        using var model = new NaptimeViewModel(host);

        model.NewHabitName = "Gym";
        model.AddCommand.Execute(null);

        Assert.Single(host.Conditions);

        model.Habits[0].DoneToday = true;

        Assert.Empty(host.Conditions);
    }

    [Fact]
    public void The_habits_survive_the_tab_being_closed_and_opened_again()
    {
        var host = Host("persist");

        using (var model = new NaptimeViewModel(host))
        {
            model.NewHabitName = "Meditate";
            model.AddCommand.Execute(null);
            model.Habits[0].Target = 7;
            model.Habits[0].DoneToday = true;
        }

        using var again = new NaptimeViewModel(host);

        var reopened = Assert.Single(again.Habits);
        Assert.Equal("Meditate", reopened.Name);
        Assert.Equal(7, reopened.Target);
        Assert.True(reopened.DoneToday);
    }

    [Fact]
    public void Search_finds_a_habit_and_lands_on_it()
    {
        var host = Host("search");
        using var model = new NaptimeViewModel(host);

        model.NewHabitName = "Drink water";
        model.AddCommand.Execute(null);

        var hit = Assert.Single(model.Search("water", 5));
        Assert.Equal("Drink water", hit.Title);

        hit.Open();
        Assert.Equal("Drink water", model.Selected?.Shown);
    }

    [Fact]
    public void While_off_answers_from_habits_and_a_hit_hands_it_to_itself()
    {
        var inner = Host("off");
        string habitId;
        using (var model = new NaptimeViewModel(inner))
        {
            model.NewHabitName = "Stretch";
            model.AddCommand.Execute(null);
            habitId = model.Habits[0].Id;
        }

        var dormant = new Dormant(inner, "meows.naptime");
        dormant.Handoffs.Reachable.Add("meows.naptime");
        var asleep = new NaptimePlugin().WhileOff(dormant);
        Assert.NotNull(asleep);

        var hit = Assert.Single(asleep.Search("stretch", 6));
        Assert.Equal("Stretch", hit.Title);

        hit.Open();
        var (to, what) = Assert.Single(dormant.Handoffs.Sent);
        Assert.Equal("meows.naptime", to);
        Assert.Equal(NaptimePlugin.ShowVerb, what.Verb);
        Assert.Equal(habitId, what.Note);
    }

    [Fact]
    public void While_off_with_nothing_kept_has_nothing_to_search()
    {
        var dormant = new Dormant(Host("empty-off"), "meows.naptime");

        Assert.Null(new NaptimePlugin().WhileOff(dormant));
    }

    [Fact]
    public void Glance_while_off_says_what_the_tab_would_say()
    {
        var inner = Host("glance-off");
        using (var model = new NaptimeViewModel(inner))
        {
            model.NewHabitName = "Walk";
            model.AddCommand.Execute(null);
            model.Habits[0].DoneToday = true;
        }

        var glance = new NaptimePlugin().GlanceWhileOff(new Dormant(inner, "meows.naptime"));

        Assert.NotNull(glance);
        Assert.False(glance!.IsTrouble);

        using var open = new NaptimeViewModel(inner);
        Assert.Equal(open.Glance(), glance);
    }

    [Fact]
    public async Task A_rule_ticks_the_named_habit_once_and_declines_the_rest()
    {
        var inner = Host("rule");
        using var model = new NaptimeViewModel(inner);
        model.NewHabitName = "Weigh myself";
        model.AddCommand.Execute(null);

        ActionRequest Asked(string action, string subject) =>
            new(action, new StoredEvent(7, DateTime.Now, "meows.weighin", "reading", subject, null,
                new Dictionary<string, string>()));

        var said = await model.Perform(Asked(NaptimePlugin.TickAction, "weigh MYSELF"), CancellationToken.None);
        Assert.Contains("Weigh myself", said);
        // Asked by a rule, not a person looking: the tick lands, the selection is untouched.
        Assert.Equal("Weigh myself", model.Habits.Single(h => h.DoneToday).Shown);

        var again = await model.Perform(Asked(NaptimePlugin.TickAction, "Weigh myself"), CancellationToken.None);
        Assert.Contains("already", again);
        Assert.Single(inner.Store.Events, e => e.Kind == "ticked");

        await Assert.ThrowsAsync<ActionDeclinedException>(() => model.Perform(Asked(NaptimePlugin.TickAction, "No such habit"), CancellationToken.None));
        await Assert.ThrowsAsync<ActionDeclinedException>(() => model.Perform(Asked("juggle", "Weigh myself"), CancellationToken.None));
    }

    /// <summary>The little a plugin gets while off: its settings and a way to hand itself the thing found.</summary>
    private sealed class Dormant(FakeHost inner, string pluginId) : IMeowsDormantHost
    {
        public string PluginId { get; } = pluginId;

        public string DataDirectory => inner.DataDirectory;

        public IMeowsText Text => MeowsText.Current;

        public IMeowsStore Store => inner.Store;

        public FakeHost.FakeHandoff Handoffs { get; } = new();

        public IMeowsHandoff Handoff => Handoffs;

        public T? LoadSettings<T>() where T : class => inner.LoadSettings<T>();
    }
}
