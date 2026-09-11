using Meows.Plugins.Abstractions;
using Meows.Plugins.Tin.Services;
using Meows.Plugins.Tin.ViewModels;

namespace Meows.Tests;

/// <summary>
/// Which account a file is about. Worked out from the file rather than asked for, because every
/// export names its own account somewhere and nobody wants to assign files by hand.
/// </summary>
public class IbanTests
{
    [Fact]
    public void An_account_printed_in_groups_of_four_is_still_one_account()
    {
        // How a PDF prints it, which reaches the parser as six separate words.
        Assert.Equal("DE49426501500001274042", Iban.In("Konto DE49 4265 0150 0001 2740 42 EUR"));
    }

    [Fact]
    public void An_account_written_solid_is_found_too()
    {
        // How a CSV column holds it.
        Assert.Equal("DE08426501501018015006", Iban.In("\"DE08426501501018015006\";\"10.09.26\""));
    }

    [Fact]
    public void A_long_number_that_is_not_an_account_is_not_one()
    {
        // A statement is full of these: mandate references, customer numbers, contract ids. The
        // checksum is the only thing standing between them and a page of invented accounts.
        Assert.Null(Iban.In("Mandatsreferenz M101024003461 1021117539167 KRANKEN"));
        Assert.Null(Iban.In("Vertrag AS-9248552375 Rechtsschutzversicherung"));
        Assert.False(Iban.IsReal("DE00426501500001274042"));
    }

    [Fact]
    public void The_first_account_named_is_the_one_the_file_is_about()
    {
        // A statement names its own account in the heading and everybody else's in the rows.
        var found = Iban.Find([
            "Kontoauszug",
            "DE49 4265 0150 0001 2740 42",
            "Ueberweisung an DE08426501501018015006",
        ]);

        Assert.Equal("DE49426501500001274042", found);
    }

    [Fact]
    public void An_account_running_straight_into_the_next_column_is_read_at_its_own_length()
    {
        // A CSV row puts the account, then a date, with nothing between them once the punctuation
        // is dropped. Trying every length longest-first found a longer string that passed the
        // checksum by luck, which is a one in ninety seven event and therefore a regular one.
        Assert.Equal("DE08426501501018015006", Iban.In("DE08426501501018015006 03.06.2026 STREAMING AG -12,99"));
    }

    [Fact]
    public void Enough_of_an_account_to_recognise_and_not_enough_to_write_down()
    {
        Assert.Equal("DE49 … 4042", Iban.Mask("DE49426501500001274042"));
        Assert.Equal("", Iban.Mask(Iban.Unknown));
    }
}

public sealed class AccountSortingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tin-acct-" + Guid.NewGuid().ToString("N")[..10]);

    public AccountSortingTests()
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

    private void Export(string name, string iban, params string[] rows) =>
        File.WriteAllText(Path.Combine(_root, name),
            "Auftragskonto;Buchungstag;Beguenstigter;Betrag\n" +
            string.Join("\n", rows.Select(r => $"{iban};{r}")) + "\n");

    private const string Hers = "DE49426501500001274042";
    private const string His = "DE08426501501018015006";

    [Fact]
    public void Files_are_sorted_into_the_accounts_they_name()
    {
        Export("a.csv", Hers, "01.08.2026;FITNESS NORD;-29,90");
        Export("b.csv", Hers, "01.09.2026;FITNESS NORD;-29,90");
        Export("c.csv", His, "01.09.2026;STREAMING AG;-12,99");

        var reading = Statement.Read(_root);

        Assert.Equal(2, reading.Accounts.Count);
        Assert.Equal(2, reading.Sources.Count(s => s.Account == Hers));
        Assert.Single(reading.Sources, s => s.Account == His);
    }

    [Fact]
    public void The_same_payment_leaving_two_accounts_on_one_day_is_two_payments()
    {
        // The dedupe exists for one file overlapping another, and it must not reach across
        // accounts: a standing order paid from both accounts is two real payments.
        Export("hers.csv", Hers, "01.09.2026;STADTWERKE NORD;-84,00");
        Export("his.csv", His, "01.09.2026;STADTWERKE NORD;-84,00");

        var reading = Statement.Read(_root);

        Assert.Equal(2, reading.Charges.Count);
        Assert.Equal(0, reading.Duplicates);
    }

    [Fact]
    public void A_file_can_be_told_which_account_it_belongs_to()
    {
        // For the exports that never say, and the ones that say it somewhere nothing looks.
        File.WriteAllText(Path.Combine(_root, "nameless.csv"),
            "Buchungstag;Beguenstigter;Betrag\n01.09.2026;FITNESS NORD;-29,90\n");

        Assert.Equal(Iban.Unknown, Assert.Single(Statement.Read(_root).Sources).Account);

        var told = Statement.Read(_root, null, new Dictionary<string, string> { ["nameless.csv"] = Hers });

        Assert.Equal(Hers, Assert.Single(told.Sources).Account);
    }

    [Fact]
    public void Picking_an_account_narrows_everything_to_it()
    {
        Export("hers.csv", Hers,
            "01.06.2026;FITNESS NORD;-29,90", "01.07.2026;FITNESS NORD;-29,90", "01.08.2026;FITNESS NORD;-29,90");
        Export("his.csv", His,
            "03.06.2026;STREAMING AG;-12,99", "03.07.2026;STREAMING AG;-12,99", "03.08.2026;STREAMING AG;-12,99");

        var host = new FakeHost(Path.Combine(_root, "data"));
        using var model = new TinViewModel(host);
        model.SetFolder(_root);

        // Everything, plus one entry for each account.
        Assert.Equal(3, model.Accounts.Count);
        Assert.True(model.HasAccounts);
        Assert.Equal(2, model.Series.Count);

        model.AccountCommand.Execute(model.Accounts.Single(a => a.Key == His));

        Assert.Single(model.Series);
        Assert.Contains("STREAMING", model.Series[0].Name, StringComparison.Ordinal);

        // And the files listed are that account's files.
        Assert.Equal("his.csv", Assert.Single(model.Sources).FileName);
    }

    [Fact]
    public void An_account_keeps_the_name_you_gave_it()
    {
        Export("hers.csv", Hers, "01.08.2026;FITNESS NORD;-29,90");
        Export("his.csv", His, "01.08.2026;STREAMING AG;-12,99");

        var host = new FakeHost(Path.Combine(_root, "data"));

        using (var model = new TinViewModel(host))
        {
            model.SetFolder(_root);
            model.AccountCommand.Execute(model.Accounts.Single(a => a.Key == Hers));
            model.AccountName = "Mama";
        }

        // A second opening reads the settings file rather than the object that wrote it.
        using var again = new TinViewModel(host);

        Assert.Equal("Mama", again.AccountName);
        Assert.Equal("Mama", again.Accounts.Single(a => a.Key == Hers).Name);

        // The name never hides which account it is.
        Assert.Equal("DE49 … 4042", again.Accounts.Single(a => a.Key == Hers).Under);
    }

    [Fact]
    public void One_account_needs_no_chooser()
    {
        Export("a.csv", Hers, "01.08.2026;FITNESS NORD;-29,90");

        var host = new FakeHost(Path.Combine(_root, "data"));
        using var model = new TinViewModel(host);
        model.SetFolder(_root);

        Assert.False(model.HasAccounts);
        Assert.Single(model.Accounts);
    }
}
