using Meows.Plugins.Tin.Services;

namespace Meows.Tests;

/// <summary>
/// Where the money went, and what a month usually does. Nothing here predicts anything: it is
/// arithmetic on months that have already happened, and the tests are mostly about which months
/// are allowed to count.
/// </summary>
public class SpendingTests
{
    private static readonly DateTime Now = new(2026, 9, 11);

    private static Charge Out(string name, string date, decimal amount) =>
        new(DateTime.Parse(date), name, -amount, "giro.csv");

    private static Charge In(string name, string date, decimal amount) =>
        new(DateTime.Parse(date), name, amount, "giro.csv");

    /// <summary>
    /// Six whole months of the same three bills and a wage, plus sixty a month of shopping.
    ///
    /// The shopping is spread over rotating shops on purpose. Sixty to the same supermarket on the
    /// seventh of every month is a subscription by every rule this plugin has, and writing it that
    /// way tests nothing about the half of a month that does not repeat.
    /// </summary>
    private static List<Charge> Year()
    {
        string[] shops = ["NETTO MARKEN-DISCOUNT", "REWE SAGT DANKE", "EDEKA NORD"];
        var charges = new List<Charge>();

        for (var month = 3; month <= 8; month++)
        {
            charges.Add(Out("MIETE HAUSVERWALTUNG", $"2026-{month:00}-01", 940m));
            charges.Add(Out("STADTWERKE NORD", $"2026-{month:00}-15", 84m));
            charges.Add(Out("STREAMING AG", $"2026-{month:00}-03", 12.99m));
            charges.Add(Out(shops[month % shops.Length], $"2026-{month:00}-{7 + month:00}", 60m));
            charges.Add(In("ARBEITGEBER GEHALT", $"2026-{month:00}-28", 2400m));
        }

        return charges;
    }

    [Fact]
    public void The_ring_is_the_outgoing_money_by_payee_biggest_first()
    {
        var slices = Spending.ByPayee(Year());

        Assert.Equal("MIETE HAUSVERWALTUNG", slices[0].Name);
        Assert.Equal(940m * 6, slices[0].Total);
        Assert.Equal("STADTWERKE NORD", slices[1].Name);

        // The wage is not a wedge. This is where money goes, not where it comes from.
        Assert.DoesNotContain(slices, s => s.Name.Contains("GEHALT", StringComparison.Ordinal));
    }

    [Fact]
    public void The_shares_add_up_to_the_whole_of_it()
    {
        var slices = Spending.ByPayee(Year());

        Assert.Equal(1.0, slices.Sum(s => s.Share), 3);
    }

    [Fact]
    public void Everything_past_the_eighth_payee_becomes_one_wedge()
    {
        // Twelve payees, because a ring of twelve wedges is a ring nobody can read. Named so that
        // no two share their first two words, since that is what the chart groups on.
        var charges = Enumerable.Range(1, 12)
            .Select(i => Out($"{Names[i - 1]} MARKT", "2026-05-02", i * 10))
            .ToList();

        var slices = Spending.ByPayee(charges);

        Assert.Equal(Spending.Wedges + 1, slices.Count);
        Assert.True(slices[^1].IsRest);

        // And the wedge is worth what the four smallest were worth together.
        Assert.Equal(10m + 20m + 30m + 40m, slices[^1].Total);
    }

    private static readonly string[] Names =
        ["ALFA", "BRAVO", "CHARLIE", "DELTA", "ECHO", "FOXTROT", "GOLF", "HOTEL", "INDIA", "JULIET", "KILO", "LIMA"];

    [Fact]
    public void Only_whole_months_count()
    {
        // Exports beginning mid month and ending mid month, which is every folder of exports.
        var charges = new List<Charge>
        {
            Out("SHOP", "2026-05-20", 10m),
            Out("SHOP", "2026-06-10", 10m),
            Out("SHOP", "2026-07-10", 10m),
            Out("SHOP", "2026-08-14", 10m),
        };

        // May is half read and August ends on the fourteenth, so June and July are all there is.
        Assert.Equal([new DateTime(2026, 6, 1), new DateTime(2026, 7, 1)], Spending.WholeMonths(charges));
    }

    [Fact]
    public void A_month_that_runs_end_to_end_counts_whole()
    {
        var charges = new List<Charge>
        {
            Out("SHOP", "2026-06-01", 10m),
            Out("SHOP", "2026-07-31", 10m),
        };

        Assert.Equal(2, Spending.WholeMonths(charges).Count);
    }

    [Fact]
    public void What_repeats_and_what_does_not_are_counted_apart()
    {
        var month = Spending.Monthly(Year(), Now, incoming: false);

        // Rent, utilities and the subscription are rhythms it recognised.
        Assert.Equal(940m + 84m + 12.99m, month.Known);

        // The shopping is not, so it is the middle month of it.
        Assert.Equal(60m, month.Typical);
        Assert.Equal(1096.99m, month.Total);
    }

    [Fact]
    public void The_middle_month_is_used_rather_than_the_average()
    {
        // One catastrophic month must not raise the estimate of every month after it.
        var charges = Year();
        charges.Add(Out("GARAGE REPAIRS", "2026-06-09", 3000m));

        var month = Spending.Monthly(charges, Now, incoming: false);

        Assert.Equal(60m, month.Typical);
    }

    [Fact]
    public void Money_coming_in_is_counted_the_same_way_when_it_is_asked_for()
    {
        var month = Spending.Monthly(Year(), Now, incoming: true);

        // A wage every month is exactly as much a rhythm as a subscription is.
        Assert.Equal(2400m, month.Known);
        Assert.Equal(0m, month.Typical);
    }

    [Fact]
    public void A_yearly_premium_counts_once_a_year_rather_than_twelve_times()
    {
        var charges = new List<Charge>
        {
            Out("DOMAIN HOST", "2024-03-01", 18m),
            Out("DOMAIN HOST", "2025-03-01", 18m),
            Out("DOMAIN HOST", "2026-03-01", 18m),
        };

        var year = Spending.Yearly(charges, Now, incoming: false);

        Assert.Equal(18m, year.Known);
    }

    [Fact]
    public void Half_a_folder_is_not_enough_to_say_what_a_month_looks_like()
    {
        // One whole month is a month, not a habit.
        var charges = new List<Charge>
        {
            Out("SHOP", "2026-07-20", 10m),
            Out("SHOP", "2026-08-10", 10m),
        };

        Assert.False(Spending.Monthly(charges, Now, incoming: false).IsUseful);
    }
}
