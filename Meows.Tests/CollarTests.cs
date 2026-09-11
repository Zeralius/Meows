using Meows.Plugins.Abstractions;
using Meows.Plugins.Collar.Services;
using Meows.Plugins.Collar.ViewModels;

namespace Meows.Tests;

/// <summary>
/// The arithmetic behind the list. Every one of these is told what today is rather than reading
/// the clock, so a run that starts before midnight cannot answer differently to the one after it.
/// </summary>
public class DatesTests
{
    private static readonly DateTime Today = new(2026, 9, 10);

    private static CollarEntry Entry(DateTime due, int repeatMonths = 0, bool done = false) =>
        new() { Title = "Something", Due = due, RepeatMonths = repeatMonths, Done = done };

    [Fact]
    public void A_date_that_has_passed_is_overdue_whatever_the_lead_time_is()
    {
        Assert.Equal(Standing.Overdue, Dates.Of(Entry(Today.AddDays(-1)), Today, 30));
        Assert.Equal(Standing.Overdue, Dates.Of(Entry(Today.AddDays(-1)), Today, 0));
    }

    [Fact]
    public void Today_counts_as_due_rather_than_as_late()
    {
        Assert.Equal(Standing.Soon, Dates.Of(Entry(Today), Today, 30));
    }

    [Fact]
    public void The_lead_time_decides_where_soon_stops()
    {
        Assert.Equal(Standing.Soon, Dates.Of(Entry(Today.AddDays(30)), Today, 30));
        Assert.Equal(Standing.Later, Dates.Of(Entry(Today.AddDays(31)), Today, 30));
    }

    [Fact]
    public void Something_that_happens_once_is_finished_with_when_it_is_dealt_with()
    {
        var entry = Entry(Today.AddDays(-3));

        Dates.Handle(entry, Today);

        Assert.True(entry.Done);
        Assert.Equal(Standing.Done, Dates.Of(entry, Today, 30));
    }

    [Fact]
    public void Something_that_repeats_moves_on_rather_than_ending()
    {
        var entry = Entry(new DateTime(2026, 9, 1), repeatMonths: 12);

        Dates.Handle(entry, Today);

        Assert.False(entry.Done);
        Assert.Equal(new DateTime(2027, 9, 1), entry.Due);
    }

    [Fact]
    public void A_repeat_that_went_unopened_for_years_lands_on_the_next_real_date()
    {
        // Advancing by one period would put it in 2024, which is a date already missed and a
        // notification that can never be cleared.
        var entry = Entry(new DateTime(2023, 3, 4), repeatMonths: 12);

        Dates.Handle(entry, Today);

        Assert.Equal(new DateTime(2027, 3, 4), entry.Due);
    }

    [Fact]
    public void The_day_of_the_month_survives_moving_on()
    {
        var entry = Entry(new DateTime(2026, 1, 31), repeatMonths: 1);

        Dates.Handle(entry, new DateTime(2026, 2, 1));

        // February has no 31st, and the insurer still uses the 31st.
        Assert.Equal(new DateTime(2026, 2, 28), entry.Due);
    }

    [Fact]
    public void Late_first_then_closest_then_the_ones_already_dealt_with()
    {
        var late = Entry(Today.AddDays(-5));
        var soon = Entry(Today.AddDays(2));
        var later = Entry(Today.AddDays(200));
        var done = Entry(Today.AddDays(-40), done: true);

        var order = Dates.InOrder([later, done, soon, late], Today, 30).ToList();

        Assert.Equal([late, soon, later, done], order);
    }
}

public class ReceiptTests
{
    private static readonly DateTime Today = new(2026, 9, 10);

    [Fact]
    public void A_file_name_becomes_a_title_a_person_would_write()
    {
        Assert.Equal("Waschmaschine Bosch", Receipt.Title("waschmaschine_bosch"));
        Assert.Equal("Waschmaschine Bosch", Receipt.Title("20240314-waschmaschine-bosch"));
    }

    [Fact]
    public void A_dropped_receipt_fills_in_most_of_an_entry()
    {
        var file = Path.Combine(Path.GetTempPath(), "meows-collar-" + Guid.NewGuid().ToString("N")[..8] + ".pdf");
        File.WriteAllText(file, "receipt");
        File.SetLastWriteTime(file, new DateTime(2026, 3, 4));

        try
        {
            var entry = Receipt.From(file, Today);

            Assert.Equal(Kind.Warranty, entry.Kind);
            Assert.Equal(file, entry.File);

            // Two years from the date on the file, which is the statutory warranty here.
            Assert.Equal(new DateTime(2028, 3, 4), entry.Due);
        }
        finally
        {
            File.Delete(file);
        }
    }
}

/// <summary>
/// The half that matters when the tab is not open: a date that has come round has to be said out
/// loud on the shell's own surface, and taken back down again when it is dealt with.
/// </summary>
public sealed class CollarNotificationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "collar-" + Guid.NewGuid().ToString("N")[..10]);

    public CollarNotificationTests()
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
            // A leftover temp folder is not worth failing a run over.
        }
    }

    [Fact]
    public void A_date_that_has_passed_is_raised_on_the_shell()
    {
        var host = new FakeHost(_root);
        using var model = new CollarViewModel(host);

        model.Add(new CollarEntry { Title = "TÜV", Due = DateTime.Today.AddDays(-2) });

        Assert.Single(host.Conditions);
    }

    [Fact]
    public void Dealing_with_the_last_one_takes_the_condition_back_down()
    {
        // A condition set and never cleared is worse than no condition at all.
        var host = new FakeHost(_root);
        using var model = new CollarViewModel(host);

        model.Add(new CollarEntry { Title = "TÜV", Due = DateTime.Today.AddDays(-2) });
        Assert.NotEmpty(host.Conditions);

        model.Selected = model.Entries.First();
        model.HandleCommand.Execute(null);

        Assert.Empty(host.Conditions);
    }

    [Fact]
    public void Something_far_off_is_not_news_yet()
    {
        var host = new FakeHost(_root);
        using var model = new CollarViewModel(host);

        model.Add(new CollarEntry { Title = "Domain", Due = DateTime.Today.AddDays(200) });

        Assert.Empty(host.Conditions);
    }

    [Fact]
    public void The_list_survives_the_tab_being_closed_and_opened_again()
    {
        var host = new FakeHost(_root);

        using (var model = new CollarViewModel(host))
            model.Add(new CollarEntry { Title = "Insurance", Due = DateTime.Today.AddDays(40), RepeatMonths = 12 });

        using var again = new CollarViewModel(host);

        var entry = Assert.Single(again.Entries);
        Assert.Equal("Insurance", entry.Title);
        Assert.Equal(12, entry.RepeatMonths);
    }

    [Fact]
    public void The_calendar_is_looked_at_again_on_a_timer_rather_than_only_when_the_tab_opens()
    {
        // Nothing here is expensive; the point of the timer is that a tab left open across
        // midnight is otherwise still showing yesterday's arithmetic.
        var host = new FakeHost(_root);
        using var model = new CollarViewModel(host);

        var scheduled = Assert.Single(host.Work.Scheduled);
        Assert.False(scheduled.RunImmediately);
        Assert.Equal(TimeSpan.FromHours(6), scheduled.Interval);
    }
}
