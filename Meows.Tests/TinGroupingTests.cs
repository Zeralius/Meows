using Meows.Plugins.Abstractions;
using Meows.Plugins.Tin.Services;
using Meows.Plugins.Tin.ViewModels;

namespace Meows.Tests;

/// <summary>
/// One payee, several things. Every case here came off a real statement: an insurer with four
/// policies and two price rises inside them, an energy company whose instalment flickers by a
/// euro, a city taking a quarterly tax and once, at a counter, a card payment.
/// </summary>
public class GroupingTests
{
    private static readonly DateTime Now = new(2026, 9, 11);

    private static Charge Out(string name, string date, decimal amount) =>
        new(DateTime.Parse(date), name, -amount, "giro.csv");

    private static IEnumerable<Charge> Monthly(string name, decimal amount, int fromMonth, int toMonth, int day = 1, int year = 2026)
    {
        for (var month = fromMonth; month <= toMonth; month++)
            yield return Out(name, $"{year}-{month:00}-{day:00}", amount);
    }

    [Fact]
    public void A_policy_that_rose_in_january_is_one_policy_and_not_two()
    {
        // Two steady runs, one after the other, at the same rhythm: the same thing at a new price.
        var charges = Monthly("ALLIANZ VERSICHERUNGS-AG", 86.08m, 1, 3)
            .Concat(Monthly("ALLIANZ VERSICHERUNGS-AG", 90.50m, 4, 9))
            .Concat(Monthly("ALLIANZ VERSICHERUNGS-AG", 17.64m, 1, 9))
            .ToList();

        var series = Recurring.Find(charges, Now);

        Assert.Equal(2, series.Count);

        var rose = series.Single(s => Math.Abs(s.LastAmount) == 90.50m);
        Assert.Equal(9, rose.Charges.Count);
        Assert.True(rose.WentUp);
        Assert.Equal(86.08m, Math.Abs(rose.PreviousAmount!.Value));
    }

    [Fact]
    public void An_instalment_that_flickers_by_a_euro_is_one_thing()
    {
        // 232 one month, 233 the next, at random. Two different amounts by arithmetic, and one
        // contract by any reading a person would give it.
        var charges = new List<Charge>();
        for (var month = 1; month <= 9; month++)
            charges.Add(Out("SWK ENERGIE", $"2026-{month:00}-01", month % 2 == 0 ? 232m : 233m));

        var found = Assert.Single(Recurring.Find(charges, Now));

        Assert.Equal(9, found.Charges.Count);
        Assert.False(found.WentUp);
    }

    [Fact]
    public void A_card_payment_at_the_counter_does_not_break_the_quarterly_tax()
    {
        var charges = new List<Charge>
        {
            Out("STADT DORSTEN", "2025-11-14", 296.63m),
            Out("STADT DORSTEN", "2026-02-14", 310.27m),
            Out("STADT DORSTEN", "2026-05-15", 310.27m),
            Out("STADT DORSTEN", "2026-06-09", 53.00m),
            Out("STADT DORSTEN", "2026-08-14", 314.48m),
        };

        var found = Assert.Single(Recurring.Find(charges, Now));

        Assert.Equal(Cadence.Quarterly, found.Cadence);
        Assert.Equal(4, found.Charges.Count);
        Assert.True(found.WentUp);
    }

    [Fact]
    public void A_one_off_correction_is_not_the_price_it_used_to_be()
    {
        // 53,45 a month, a 2,67 adjustment in January, then 56,12. The rise is 2,67, not 53,45.
        var charges = Monthly("ERGO Vorsorge", 53.45m, 1, 4).ToList();
        charges.Add(Out("ERGO Vorsorge", "2026-04-01", 2.67m));
        charges.AddRange(Monthly("ERGO Vorsorge", 56.12m, 5, 9));

        var found = Assert.Single(Recurring.Find(charges, Now));

        Assert.Equal(53.45m, Math.Abs(found.PreviousAmount!.Value));
        Assert.Equal(2.67m, found.Rise);
    }

    [Fact]
    public void A_payee_that_sometimes_writes_its_street_is_one_payee()
    {
        // Split by spelling, each half has half the months and one of them looks cancelled.
        var charges = new List<Charge>();
        for (var month = 1; month <= 9; month++)
            charges.Add(Out(month % 3 == 0 ? "STRATO Otto-Ostrowski-Strasse Berlin" : "STRATO", $"2026-{month:00}-05", 9m));

        var found = Assert.Single(Recurring.Find(charges, Now));

        Assert.Equal(9, found.Charges.Count);
        Assert.False(found.Lapsed);
    }

    [Fact]
    public void Something_that_stopped_is_listed_and_not_added()
    {
        var charges = Monthly("OLD GYM", 19.90m, 1, 3).ToList();

        var found = Assert.Single(Recurring.Find(charges, Now));

        Assert.True(found.Lapsed);
        Assert.Equal(19.90m, found.PerMonth);
        Assert.Equal(0m, found.Ongoing);
    }
}

public sealed class FamilyRowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tin-fam-" + Guid.NewGuid().ToString("N")[..10]);

    public FamilyRowTests()
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

    private TinViewModel Model()
    {
        // Up to the month the tests run in, so nothing here has had time to look cancelled.
        var rows = new List<string>();
        for (var month = 4; month <= 9; month++)
        {
            rows.Add($"01.{month:00}.2026;SWK ENERGIE GmbH;-100,00");
            rows.Add($"01.{month:00}.2026;SWK ENERGIE GmbH;-50,00");
            rows.Add($"01.{month:00}.2026;STRATO;-9,00");
        }

        File.WriteAllText(Path.Combine(_root, "giro.csv"),
            "Buchungstag;Beguenstigter;Betrag" + Environment.NewLine +
            string.Join(Environment.NewLine, rows) + Environment.NewLine);

        var model = new TinViewModel(new FakeHost(Path.Combine(_root, "data")));
        model.SetFolder(_root);
        return model;
    }

    [Fact]
    public void A_company_billing_two_things_is_one_row_with_the_sum_and_the_parts_under_it()
    {
        using var model = Model();

        // Three subscriptions, drawn as four rows: the company, its two parts, and STRATO alone.
        Assert.Equal(3, model.Series.Count);
        Assert.Equal(4, model.Rows.Count);

        var family = Assert.IsType<FamilyViewModel>(model.Rows[0]);
        Assert.Equal(150m, family.PerMonth);
        Assert.Equal(2, family.Members.Count);

        Assert.True(Assert.IsType<SeriesViewModel>(model.Rows[1]).IsChild);
        Assert.True(Assert.IsType<SeriesViewModel>(model.Rows[2]).IsChild);
        Assert.False(Assert.IsType<SeriesViewModel>(model.Rows[3]).IsChild);
    }

    [Fact]
    public void The_parts_come_biggest_first_under_their_company()
    {
        using var model = Model();

        Assert.Equal(-100m, Assert.IsType<SeriesViewModel>(model.Rows[1]).Series.LastAmount);
        Assert.Equal(-50m, Assert.IsType<SeriesViewModel>(model.Rows[2]).Series.LastAmount);
    }

    [Fact]
    public void Putting_away_the_company_puts_away_everything_under_it()
    {
        using var model = Model();

        model.Selected = model.Rows[0];
        model.IgnoreCommand.Execute(null);

        // Only STRATO is left showing.
        Assert.Single(model.Series);
        Assert.Equal("STRATO", model.Series[0].Name);
    }

    [Fact]
    public void Selecting_the_company_shows_every_charge_under_it()
    {
        using var model = Model();

        model.Selected = model.Rows[0];

        Assert.Equal(12, model.Charges.Count);
    }
}
