using Meows.Plugins.Tin.Services;
using Meows.Plugins.Tin.ViewModels;

namespace Meows.Tests;

/// <summary>
/// Reading the two fields every bank writes differently. Everything here is a real shape taken
/// from a real export rather than an invented one, because the whole difficulty of this plugin is
/// that the files disagree with each other.
/// </summary>
public class MoneyTests
{
    [Theory]
    [InlineData("12,34", 12.34)]
    [InlineData("12.34", 12.34)]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("1,234.56", 1234.56)]
    [InlineData("-8,99", -8.99)]
    [InlineData("8,99-", -8.99)]
    [InlineData("(8,99)", -8.99)]
    [InlineData("-12,34 EUR", -12.34)]
    [InlineData("€ 4,90", 4.90)]
    [InlineData("1.234", 1234)]
    [InlineData("0", 0)]
    public void An_amount_reads_the_same_whichever_country_wrote_it(string text, double expected)
    {
        Assert.True(Money.TryAmount(text, out var amount), text);
        Assert.Equal((decimal)expected, amount);
    }

    [Fact]
    public void Something_that_is_not_a_number_is_refused_rather_than_read_as_zero()
    {
        // A zero here would become a charge, and a folder full of them would become a series.
        Assert.False(Money.TryAmount("", out _));
        Assert.False(Money.TryAmount("   ", out _));
        Assert.False(Money.TryAmount("Verwendungszweck", out _));
    }

    [Theory]
    [InlineData("01.09.2026", 2026, 9, 1)]
    [InlineData("1.9.2026", 2026, 9, 1)]
    [InlineData("2026-09-01", 2026, 9, 1)]
    [InlineData("01/09/2026", 2026, 9, 1)]
    [InlineData("20260901", 2026, 9, 1)]
    public void A_date_reads_european_first(string text, int year, int month, int day)
    {
        Assert.True(Money.TryDate(text, out var date), text);
        Assert.Equal(new DateTime(year, month, day), date);
    }

    [Fact]
    public void A_time_of_day_is_dropped_because_a_charge_is_a_day()
    {
        Assert.True(Money.TryDate("01.09.2026 14:32", out var date));
        Assert.Equal(new DateTime(2026, 9, 1), date);
    }
}

public class CsvTests
{
    private const string German =
        "Kontonummer;DE02120300000000202051\n" +
        "\n" +
        "Buchungstag;Verwendungszweck;Beguenstigter/Zahlungspflichtiger;Betrag;Waehrung\n" +
        "01.09.2026;MONATSBEITRAG;FITNESS NORD GMBH;-29,90;EUR\n" +
        "02.09.2026;Rechnung 4711;Stadtwerke;-84,00;EUR\n";

    [Fact]
    public void The_header_is_found_under_the_preamble_the_bank_put_above_it()
    {
        var table = Csv.Read(German);

        // Taking the first row on faith gives a table whose only column is an account number.
        Assert.Equal(5, table.Header.Count);
        Assert.Equal("Buchungstag", table.Header[0]);
        Assert.Equal(2, table.Rows.Count);
    }

    [Fact]
    public void The_delimiter_comes_from_the_file_rather_than_from_a_setting()
    {
        Assert.Equal(';', Csv.Delimiter(German));
        Assert.Equal(',', Csv.Delimiter("date,payee,amount\n2026-09-01,Shop,-4.99\n"));
        Assert.Equal('\t', Csv.Delimiter("date\tpayee\tamount\n2026-09-01\tShop\t-4.99\n"));
    }

    [Fact]
    public void A_quoted_field_may_hold_the_delimiter_and_a_newline()
    {
        var table = Csv.Read("date,payee,amount\n2026-09-01,\"Shop, the\nsecond line\",-4.99\n");

        Assert.Single(table.Rows);
        Assert.Equal("Shop, the\nsecond line", table.Rows[0][1]);
    }

    [Fact]
    public void The_signature_is_the_header_so_two_exports_from_one_bank_share_it()
    {
        var first = Csv.Read(German);
        var second = Csv.Read(German.Replace("01.09.2026", "01.10.2026"));

        Assert.Equal(first.Signature, second.Signature);
        Assert.NotEqual(first.Signature, Csv.Read("date,payee,amount\n2026-09-01,Shop,-4.99\n").Signature);
    }

    [Fact]
    public void Columns_are_guessed_from_the_heading_words()
    {
        var map = Columns.Guess(Csv.Read(German).Header);

        Assert.True(map.IsComplete);
        Assert.Equal(0, map.Date);
        Assert.Equal(3, map.Amount);

        // Who the money went to beats what the transfer said, because a payee is what repeats.
        Assert.Equal(2, map.Name);
    }

    [Fact]
    public void A_heading_that_is_the_word_beats_one_that_merely_contains_it()
    {
        // A Sparkasse export has "Lastschrift Ursprungsbetrag" eight columns to the left of
        // "Betrag", and it is empty on every row that is not a returned direct debit. Taking the
        // leftmost match made the whole file unreadable, which is how this was found.
        var header = new[]
        {
            "Auftragskonto", "Buchungstag", "Valutadatum", "Buchungstext", "Verwendungszweck",
            "Glaeubiger ID", "Mandatsreferenz", "Kundenreferenz (End-to-End)", "Sammlerreferenz",
            "Lastschrift Ursprungsbetrag", "Auslagenersatz Ruecklastschrift",
            "Beguenstigter/Zahlungspflichtiger", "Kontonummer/IBAN", "BIC (SWIFT-Code)", "Betrag",
            "Waehrung", "Info",
        };

        var map = Columns.Guess(header);

        Assert.Equal(1, map.Date);
        Assert.Equal(11, map.Name);
        Assert.Equal(14, map.Amount);
    }

    [Fact]
    public void What_the_payment_was_for_is_kept_beside_who_took_it()
    {
        // Both columns exist in a real export, and they answer different questions: the payee
        // says who, the purpose says which of their three bills this one is.
        var map = Columns.Guess(["Buchungstag", "Verwendungszweck", "Beguenstigter", "Betrag"]);

        Assert.Equal(2, map.Name);
        Assert.Equal(1, map.Reference);
    }

    [Fact]
    public void An_export_with_only_one_text_column_spends_it_on_the_name()
    {
        // A name and no reference beats a reference and no name.
        var map = Columns.Guess(["Datum", "Verwendungszweck", "Betrag"]);

        Assert.Equal(1, map.Name);
        Assert.Equal(-1, map.Reference);
    }

    [Fact]
    public void A_header_nobody_recognises_is_incomplete_rather_than_wrong()
    {
        // Better an empty mapping the user can point at the right columns than three confident
        // guesses that quietly produce a plausible and wrong answer.
        var map = Columns.Guess(["a", "b", "c"]);

        Assert.False(map.IsComplete);
    }
}

public sealed class StatementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tin-" + Guid.NewGuid().ToString("N")[..10]);

    public StatementTests() => Directory.CreateDirectory(_root);

    private void Export(string name, string body) => File.WriteAllText(Path.Combine(_root, name),
        "Buchungstag;Beguenstigter;Betrag\n" + body);

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
    public void Every_csv_in_the_folder_is_read_into_one_list()
    {
        Export("august.csv", "01.08.2026;FITNESS NORD;-29,90\n");
        Export("september.csv", "01.09.2026;FITNESS NORD;-29,90\n");

        var reading = Statement.Read(_root);

        Assert.Equal(2, reading.Sources.Count);
        Assert.Equal(2, reading.Charges.Count);
        Assert.All(reading.Sources, s => Assert.True(s.IsUnderstood));
    }

    [Fact]
    public void A_charge_in_two_overlapping_exports_is_only_counted_once()
    {
        // Asking for the last ninety days twice in a month means most of one file is already in
        // the other, and a subscription counted twice looks like one that doubled in price.
        Export("first.csv", "01.08.2026;FITNESS NORD;-29,90\n01.09.2026;FITNESS NORD;-29,90\n");
        Export("second.csv", "01.09.2026;FITNESS NORD;-29,90\n01.10.2026;FITNESS NORD;-29,90\n");

        var reading = Statement.Read(_root);

        Assert.Equal(3, reading.Charges.Count);
        Assert.Equal(1, reading.Duplicates);
    }

    [Fact]
    public void A_file_whose_columns_are_not_recognised_says_so_instead_of_being_skipped_silently()
    {
        File.WriteAllText(Path.Combine(_root, "odd.csv"), "a;b;c\n1;2;3\n");

        var reading = Statement.Read(_root);

        var source = Assert.Single(reading.Sources);
        Assert.False(source.IsUnderstood);
        Assert.Equal(Statement.ProblemColumns, source.Problem);
    }

    [Fact]
    public void A_remembered_mapping_beats_the_guess()
    {
        File.WriteAllText(Path.Combine(_root, "odd.csv"), "a;b;c\n01.09.2026;SOMEONE;-5,00\n");

        var signature = Csv.ReadFile(Path.Combine(_root, "odd.csv")).Signature;
        var reading = Statement.Read(_root, new Dictionary<string, ColumnMap>
        {
            [signature] = new(0, 1, 2),
        });

        var charge = Assert.Single(reading.Charges);
        Assert.Equal("SOMEONE", charge.Name);
        Assert.Equal(-5.00m, charge.Amount);
    }

    [Fact]
    public void The_reference_travels_with_the_charge()
    {
        File.WriteAllText(Path.Combine(_root, "giro.csv"),
            "Buchungstag;Verwendungszweck;Beguenstigter;Betrag\n" +
            "01.09.2026;VK 21428908 Strom;SWK ENERGIE GmbH;-233,00\n");

        var charge = Assert.Single(Statement.Read(_root).Charges);

        Assert.Equal("SWK ENERGIE GmbH", charge.Name);
        Assert.Equal("VK 21428908 Strom", charge.Reference);
    }

    [Fact]
    public void A_pdf_that_cannot_be_opened_is_listed_with_the_reason()
    {
        // Skipping a file quietly leaves nothing on screen and no reason for it, which reads as
        // a broken plugin rather than as an unreadable file.
        File.WriteAllText(Path.Combine(_root, "kontoauszug.pdf"), "%PDF-1.7 not really");

        var reading = Statement.Read(_root);

        var source = Assert.Single(reading.Sources);
        Assert.Equal("kontoauszug.pdf", source.FileName);
        Assert.False(source.IsUnderstood);
    }

    [Fact]
    public void A_spreadsheet_is_refused_the_same_way_and_says_which_formats_are_read()
    {
        File.WriteAllText(Path.Combine(_root, "umsaetze.xlsx"), "not really a workbook");

        Assert.Equal(Statement.ProblemNotData, Assert.Single(Statement.Read(_root).Sources).Problem);
    }

    [Fact]
    public void Everything_else_in_the_folder_is_left_alone()
    {
        // A refused file is one that is plainly a statement. Listing the wallpaper as a problem
        // would be noise, and a folder of exports usually has some of both.
        Export("august.csv", "01.08.2026;FITNESS NORD;-29,90\n");
        File.WriteAllText(Path.Combine(_root, "notes.png"), "not really an image");
        File.WriteAllText(Path.Combine(_root, "desktop.ini"), "[.ShellClassInfo]");

        Assert.Single(Statement.Read(_root).Sources);
    }

    [Fact]
    public void Nothing_in_the_folder_is_written_to()
    {
        Export("august.csv", "01.08.2026;FITNESS NORD;-29,90\n");
        var before = Directory.GetFiles(_root).Select(f => (f, new FileInfo(f).LastWriteTimeUtc)).ToList();

        Statement.Read(_root);

        // These are the most private files this app is ever pointed at. Read only is a promise,
        // so it is worth a test rather than a comment.
        var after = Directory.GetFiles(_root).Select(f => (f, new FileInfo(f).LastWriteTimeUtc)).ToList();
        Assert.Equal(before, after);
    }
}

public class RecurringTests
{
    private static readonly DateTime Now = new(2026, 9, 10);

    private static Charge Charge(string name, string date, decimal amount) =>
        new(DateTime.Parse(date), name, amount, "export.csv");

    [Fact]
    public void The_same_payee_every_month_is_a_subscription()
    {
        var series = Recurring.Find(
        [
            Charge("FITNESS NORD GMBH", "2026-06-01", -29.90m),
            Charge("FITNESS NORD GMBH", "2026-07-01", -29.90m),
            Charge("FITNESS NORD GMBH", "2026-08-01", -29.90m),
        ], Now);

        var found = Assert.Single(series);
        Assert.Equal(Cadence.Monthly, found.Cadence);
        Assert.Equal(-29.90m, found.LastAmount);
        Assert.Equal(29.90m, found.PerMonth);
    }

    [Fact]
    public void Twice_is_a_coincidence()
    {
        var series = Recurring.Find(
        [
            Charge("SOME SHOP", "2026-07-01", -12.00m),
            Charge("SOME SHOP", "2026-08-01", -12.00m),
        ], Now);

        Assert.Empty(series);
    }

    [Fact]
    public void Three_unrelated_visits_to_the_same_shop_are_not_a_subscription()
    {
        var series = Recurring.Find(
        [
            Charge("CORNER SHOP", "2026-07-02", -4.20m),
            Charge("CORNER SHOP", "2026-07-19", -11.30m),
            Charge("CORNER SHOP", "2026-09-02", -7.80m),
        ], Now);

        Assert.Empty(series);
    }

    [Fact]
    public void One_late_month_does_not_break_a_subscription()
    {
        var series = Recurring.Find(
        [
            Charge("STREAMING AG", "2026-04-03", -9.99m),
            Charge("STREAMING AG", "2026-05-03", -9.99m),
            Charge("STREAMING AG", "2026-06-08", -9.99m),
            Charge("STREAMING AG", "2026-07-03", -9.99m),
            Charge("STREAMING AG", "2026-08-03", -9.99m),
        ], Now);

        Assert.Single(series);
    }

    [Fact]
    public void A_price_rise_is_measured_against_what_it_used_to_be()
    {
        var series = Recurring.Find(
        [
            Charge("STREAMING AG", "2026-06-03", -9.99m),
            Charge("STREAMING AG", "2026-07-03", -9.99m),
            Charge("STREAMING AG", "2026-08-03", -12.99m),
        ], Now);

        var found = Assert.Single(series);
        Assert.True(found.WentUp);
        Assert.Equal(3.00m, found.Rise);
    }

    [Fact]
    public void A_cent_of_movement_is_not_a_price_rise()
    {
        var series = Recurring.Find(
        [
            Charge("CLOUD INC", "2026-06-03", -5.00m),
            Charge("CLOUD INC", "2026-07-03", -5.00m),
            Charge("CLOUD INC", "2026-08-03", -5.02m),
        ], Now);

        Assert.False(Assert.Single(series).WentUp);
    }

    [Fact]
    public void Something_that_stopped_coming_is_said_to_have_stopped()
    {
        var series = Recurring.Find(
        [
            Charge("OLD GYM", "2026-01-05", -19.90m),
            Charge("OLD GYM", "2026-02-05", -19.90m),
            Charge("OLD GYM", "2026-03-05", -19.90m),
        ], Now);

        var found = Assert.Single(series);
        Assert.True(found.Lapsed);
        Assert.Equal(new DateTime(2026, 4, 5), found.Due);
    }

    [Fact]
    public void A_yearly_charge_is_still_within_its_grace_period_in_the_same_month()
    {
        var series = Recurring.Find(
        [
            Charge("DOMAIN HOST", "2023-09-01", -18.00m),
            Charge("DOMAIN HOST", "2024-09-01", -18.00m),
            Charge("DOMAIN HOST", "2025-09-01", -18.00m),
        ], Now);

        var found = Assert.Single(series);
        Assert.Equal(Cadence.Yearly, found.Cadence);
        Assert.False(found.Lapsed);
        Assert.Equal(1.50m, found.PerMonth);
    }

    [Fact]
    public void Money_coming_in_is_not_a_subscription()
    {
        // Wages repeat as reliably as anything, and are not what this tab is asking about.
        var series = Recurring.Find(
        [
            Charge("EMPLOYER", "2026-06-30", 2400m),
            Charge("EMPLOYER", "2026-07-31", 2400m),
            Charge("EMPLOYER", "2026-08-31", 2400m),
        ], Now);

        Assert.Empty(series);
    }

    [Fact]
    public void The_reference_number_that_changes_every_month_is_not_part_of_the_name()
    {
        // Same payee, different mandate reference each time. Grouping on the raw text would make
        // these three unrelated one-offs.
        var series = Recurring.Find(
        [
            Charge("SEPA LASTSCHRIFT STADTWERKE NORD RE 20260601", "2026-06-01", -84.00m),
            Charge("SEPA LASTSCHRIFT STADTWERKE NORD RE 20260701", "2026-07-01", -84.00m),
            Charge("SEPA LASTSCHRIFT STADTWERKE NORD RE 20260801", "2026-08-01", -84.00m),
        ], Now);

        Assert.Single(series);
    }

    [Fact]
    public void How_the_money_moved_is_not_who_took_it()
    {
        // Left in, every direct debit in the account groups under one enormous LASTSCHRIFT.
        Assert.Equal("STADTWERKE NORD", Recurring.KeyOf("SEPA LASTSCHRIFT STADTWERKE NORD RE 4711"));
        Assert.NotEqual(
            Recurring.KeyOf("SEPA LASTSCHRIFT STADTWERKE NORD"),
            Recurring.KeyOf("SEPA LASTSCHRIFT FITNESS NORD"));
    }

    [Fact]
    public void The_name_on_the_row_loses_the_noise_but_keeps_the_bank_s_own_spelling()
    {
        // Otherwise the row reads SEPA LASTSCHRIFT STADTWERKE NORD RE 20260801, where everything
        // but two words is either how the money moved or a reference that changes next month.
        Assert.Equal("Stadtwerke Nord", Recurring.NameOf("SEPA Lastschrift Stadtwerke Nord RE 20260801"));

        // Banks join the filler words up, and a hyphenated pair has to be judged by its parts or
        // it walks straight through the filter.
        Assert.Equal("Stadtwerke Nord", Recurring.NameOf("SEPA-LASTSCHRIFT Stadtwerke Nord"));

        // Nothing but a reference. The raw text beats an empty row.
        Assert.Equal("4711/2026", Recurring.NameOf("4711/2026"));
    }

    [Fact]
    public void A_series_shows_the_newest_reference_rather_than_a_merged_one()
    {
        var series = Assert.Single(Recurring.Find(
        [
            new Charge(DateTime.Parse("2026-06-15"), "SWK ENERGIE", -233m, "giro.csv", "Abschlag 06/2026"),
            new Charge(DateTime.Parse("2026-07-15"), "SWK ENERGIE", -233m, "giro.csv", "Abschlag 07/2026"),
            new Charge(DateTime.Parse("2026-08-15"), "SWK ENERGIE", -233m, "giro.csv", "Abschlag 08/2026"),
        ], Now));

        // They differ every month, so the newest is the one that matches what the bank is showing.
        Assert.Equal("Abschlag 08/2026", series.Reference);
    }

    [Fact]
    public void One_payee_taking_three_policies_is_three_subscriptions()
    {
        // An insurer takes three contracts from the same account on the same day every month.
        // Read as one payee that is not a rhythm at all: three payments land together, then
        // nothing for a month, and the gaps read zero, zero, thirty.
        var charges = new List<Charge>();
        for (var month = 4; month <= 8; month++)
        {
            charges.Add(Charge("ALLIANZ VERSICHERUNGS-AG", $"2026-{month:00}-01", -90.50m));
            charges.Add(Charge("ALLIANZ VERSICHERUNGS-AG", $"2026-{month:00}-01", -16.93m));
            charges.Add(Charge("ALLIANZ VERSICHERUNGS-AG", $"2026-{month:00}-01", -9.19m));
        }

        var series = Recurring.Find(charges, Now);

        Assert.Equal(3, series.Count);
        Assert.All(series, s => Assert.Equal(Cadence.Monthly, s.Cadence));
        Assert.Equal([9.19m, 16.93m, 90.50m], series.Select(s => Math.Abs(s.LastAmount)).Order());
    }

    [Fact]
    public void A_price_rise_is_not_split_into_two_subscriptions()
    {
        // The other half of the same rule. A rise also produces two steady amounts, but they run
        // one after the other rather than alongside, and splitting it would report one thing
        // cancelled and another started.
        var charges = new List<Charge>();
        for (var month = 1; month <= 4; month++)
            charges.Add(Charge("STREAMING AG", $"2026-{month:00}-03", -9.99m));
        for (var month = 5; month <= 8; month++)
            charges.Add(Charge("STREAMING AG", $"2026-{month:00}-03", -12.99m));

        var found = Assert.Single(Recurring.Find(charges, Now));

        Assert.True(found.WentUp);
        Assert.Equal(-12.99m, found.LastAmount);
    }

    [Fact]
    public void A_month_is_added_as_a_month_rather_than_as_thirty_days()
    {
        // A subscription taken on the 31st stays at the end of the month.
        Assert.Equal(new DateTime(2026, 2, 28), Recurring.Next(new DateTime(2026, 1, 31), Cadence.Monthly));
        Assert.Equal(new DateTime(2027, 1, 15), Recurring.Next(new DateTime(2026, 1, 15), Cadence.Yearly));
    }
}

/// <summary>
/// What the tab says when a folder produced nothing. An empty tab that gives no reason reads as
/// a broken plugin, and the commonest way to arrive at one is a folder of PDFs.
/// </summary>
public sealed class TinAdviceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tin-vm-" + Guid.NewGuid().ToString("N")[..10]);
    private readonly string _folder;

    public TinAdviceTests()
    {
        _folder = Path.Combine(_root, "exports");
        Directory.CreateDirectory(_folder);
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
        var model = new TinViewModel(new FakeHost(Path.Combine(_root, "data")));
        model.SetFolder(_folder);
        return model;
    }

    [Fact]
    public void A_folder_of_pdfs_says_what_to_ask_the_bank_for()
    {
        File.WriteAllText(Path.Combine(_folder, "kontoauszug-2026-08.pdf"), "%PDF-1.7");

        using var model = Model();

        Assert.True(model.HasAdvice);
        Assert.Contains("CSV", model.Advice!, StringComparison.Ordinal);

        // And the file itself is in the list rather than missing from it.
        Assert.Single(model.Sources);
    }

    [Fact]
    public void An_empty_folder_says_where_the_csv_export_usually_lives()
    {
        using var model = Model();

        Assert.True(model.HasAdvice);
    }

    [Fact]
    public void A_folder_that_worked_says_nothing()
    {
        File.WriteAllText(Path.Combine(_folder, "giro.csv"),
            "Buchungstag;Beguenstigter;Betrag\n01.08.2026;FITNESS NORD;-29,90\n");

        using var model = Model();

        Assert.False(model.HasAdvice);
    }
}
