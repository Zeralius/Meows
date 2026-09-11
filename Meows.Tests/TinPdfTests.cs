using Meows.Plugins.Tin.Services;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Meows.Tests;

/// <summary>
/// Reading a statement out of a PDF.
///
/// The statements are built here rather than checked in, at real coordinates in the layouts banks
/// actually use, because the whole difficulty is that a PDF has no columns: it has ink at
/// positions, and every rule in the parser is a rule about where banks put the ink.
/// </summary>
public sealed class TinPdfTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tin-pdf-" + Guid.NewGuid().ToString("N")[..10]);

    public TinPdfTests() => Directory.CreateDirectory(_root);

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

    /// <summary>One thing to print, at a position on the page.</summary>
    private readonly record struct Ink(double X, double Y, string Text);

    private string Pdf(string name, params Ink[] ink)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(595, 842);

        foreach (var piece in ink)
            page.AddText(piece.Text, 10, new PdfPoint(piece.X, piece.Y), font);

        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    /// <summary>
    /// The commonest German layout: two dates, a reference, the amount, and an S or an H saying
    /// which direction it went. The booking dates carry no year at all.
    /// </summary>
    private string SparkasseStyle(string name = "auszug.pdf") => Pdf(name,
        new Ink(60, 800, "Kontoauszug Nr. 9/2026 vom 01.09.2026"),
        new Ink(60, 780, "Buchungstag Wert Vorgang/Verwendungszweck Betrag"),

        new Ink(60, 750, "01.09."), new Ink(105, 750, "01.09."),
        new Ink(150, 750, "SEPA-LASTSCHRIFT FITNESS NORD GMBH"),
        new Ink(470, 750, "29,90"), new Ink(520, 750, "S"),

        new Ink(60, 735, "03.09."), new Ink(105, 735, "03.09."),
        new Ink(150, 735, "STREAMING AG ABO 4711"),
        new Ink(470, 735, "12,99"), new Ink(520, 735, "S"),

        new Ink(60, 720, "30.09."), new Ink(105, 720, "30.09."),
        new Ink(150, 720, "ARBEITGEBER GEHALT"),
        new Ink(455, 720, "2.400,00"), new Ink(520, 720, "H"));

    [Fact]
    public void Words_go_back_into_the_lines_a_person_would_read()
    {
        var lines = PdfLines.Read(SparkasseStyle());

        // A PDF has no lines. If this ever stops working, nothing below it can work either.
        Assert.Contains(lines, l => l.Text.StartsWith("01.09. 01.09. SEPA-LASTSCHRIFT", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Text.Contains("2.400,00", StringComparison.Ordinal));
    }

    [Fact]
    public void An_s_means_it_went_out_and_an_h_means_it_came_in()
    {
        var reading = PdfStatement.Read(SparkasseStyle(), new DateTime(2026, 9, 30));

        Assert.Null(reading.Problem);
        Assert.Equal(3, reading.Charges.Count);

        Assert.Equal(-29.90m, reading.Charges.Single(c => c.Name.Contains("FITNESS")).Amount);
        Assert.Equal(2400.00m, reading.Charges.Single(c => c.Name.Contains("GEHALT")).Amount);
    }

    [Fact]
    public void A_booking_day_with_no_year_takes_it_from_the_statement()
    {
        var reading = PdfStatement.Read(SparkasseStyle(), new DateTime(2020, 1, 1));

        // The year is nowhere near the rows. It is in the heading, which is where German
        // statements always leave it.
        Assert.Equal(new DateTime(2026, 9, 1), reading.Charges.Single(c => c.Name.Contains("FITNESS")).Date);
    }

    [Fact]
    public void December_bookings_on_a_january_statement_belong_to_the_year_before()
    {
        // The rollover that quietly puts a whole month eleven months into the future.
        Assert.Equal(new DateTime(2025, 12, 28),
            PdfStatement.Settle(28, 12, null, new DateTime(2026, 1, 5)));

        Assert.Equal(new DateTime(2026, 1, 3),
            PdfStatement.Settle(3, 1, null, new DateTime(2026, 1, 5)));
    }

    [Fact]
    public void Separate_debit_and_credit_columns_say_the_direction_by_themselves()
    {
        // No minus, no S, no H. The only thing saying which way the money went is which column
        // the number is printed in.
        var path = Pdf("spalten.pdf",
            new Ink(60, 800, "Umsatzanzeige 01.08.2026"),
            new Ink(60, 780, "Datum Verwendungszweck Soll Haben"),

            new Ink(60, 750, "01.08.2026"), new Ink(150, 750, "MIETE HAUSVERWALTUNG"),
            new Ink(380, 750, "940,00"),

            new Ink(60, 735, "02.08.2026"), new Ink(150, 735, "FITNESS NORD GMBH"),
            new Ink(380, 735, "29,90"),

            new Ink(60, 720, "31.08.2026"), new Ink(150, 720, "ARBEITGEBER GEHALT"),
            new Ink(480, 720, "2.400,00"));

        var reading = PdfStatement.Read(path, new DateTime(2026, 8, 31));

        Assert.Null(reading.Problem);
        Assert.Equal(-940.00m, reading.Charges.Single(c => c.Name.Contains("MIETE")).Amount);
        Assert.Equal(2400.00m, reading.Charges.Single(c => c.Name.Contains("GEHALT")).Amount);
    }

    [Fact]
    public void A_running_balance_beside_the_amount_is_not_mistaken_for_the_amount()
    {
        // The one mistake here that produces numbers looking entirely plausible. An amount and a
        // balance appear together on nearly every row; a debit and a credit never do.
        var path = Pdf("saldo.pdf",
            new Ink(60, 800, "Kontoauszug 01.08.2026"),
            new Ink(60, 780, "Datum Text Betrag Saldo"),

            new Ink(60, 750, "01.08.2026"), new Ink(150, 750, "FITNESS NORD GMBH"),
            new Ink(380, 750, "-29,90"), new Ink(480, 750, "1.970,10"),

            new Ink(60, 735, "02.08.2026"), new Ink(150, 735, "STREAMING AG"),
            new Ink(380, 735, "-12,99"), new Ink(480, 735, "1.957,11"),

            new Ink(60, 720, "03.08.2026"), new Ink(150, 720, "STADTWERKE NORD"),
            new Ink(380, 720, "-84,00"), new Ink(480, 720, "1.873,11"));

        var reading = PdfStatement.Read(path, new DateTime(2026, 8, 31));

        Assert.Equal(3, reading.Charges.Count);
        Assert.All(reading.Charges, c => Assert.True(Math.Abs(c.Amount) < 100, c.Amount.ToString()));
    }

    [Fact]
    public void A_wrapped_payment_reference_stays_with_the_row_above_it()
    {
        var path = Pdf("umbruch.pdf",
            new Ink(60, 800, "Kontoauszug 01.08.2026"),
            new Ink(60, 750, "01.08.2026"), new Ink(150, 750, "STADTWERKE NORD ABSCHLAG"),
            new Ink(380, 750, "-84,00"),
            new Ink(150, 736, "KUNDENNUMMER 998877 RECHNUNG AUGUST"));

        var reading = PdfStatement.Read(path, new DateTime(2026, 8, 31));

        var charge = Assert.Single(reading.Charges);
        Assert.Contains("KUNDENNUMMER", charge.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void The_opening_and_closing_balance_are_not_payments()
    {
        // Left in, these become the two largest subscriptions in the list.
        var path = Pdf("saldozeilen.pdf",
            new Ink(60, 800, "Kontoauszug 01.08.2026"),
            new Ink(60, 760, "01.08.2026"), new Ink(150, 760, "ALTER KONTOSTAND"),
            new Ink(380, 760, "-2.000,00"),
            new Ink(60, 745, "02.08.2026"), new Ink(150, 745, "FITNESS NORD GMBH"),
            new Ink(380, 745, "-29,90"),
            new Ink(60, 730, "31.08.2026"), new Ink(150, 730, "NEUER KONTOSTAND"),
            new Ink(380, 730, "-1.970,10"));

        var reading = PdfStatement.Read(path, new DateTime(2026, 8, 31));

        var charge = Assert.Single(reading.Charges);
        Assert.Contains("FITNESS", charge.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pdf_that_is_not_a_statement_says_so_rather_than_inventing_charges()
    {
        var path = Pdf("brief.pdf",
            new Ink(60, 800, "Sehr geehrter Kunde,"),
            new Ink(60, 780, "wir freuen uns, Ihnen mitteilen zu koennen, dass sich nichts aendert."));

        var reading = PdfStatement.Read(path, new DateTime(2026, 8, 31));

        Assert.Empty(reading.Charges);
        Assert.Equal(PdfStatement.ProblemNothing, reading.Problem);
    }

    [Fact]
    public void Rows_with_no_direction_at_all_are_refused_rather_than_guessed()
    {
        // One amount column, no signs, no markers. Guessing "out" would turn a salary into the
        // largest subscription on the tab.
        var path = Pdf("richtungslos.pdf",
            new Ink(60, 800, "Kontoauszug 01.08.2026"),
            new Ink(60, 750, "01.08.2026"), new Ink(150, 750, "FITNESS NORD GMBH"),
            new Ink(380, 750, "29,90"),
            new Ink(60, 735, "02.08.2026"), new Ink(150, 735, "STREAMING AG"),
            new Ink(380, 735, "12,99"));

        var reading = PdfStatement.Read(path, new DateTime(2026, 8, 31));

        Assert.Empty(reading.Charges);
        Assert.Equal(PdfStatement.ProblemSigns, reading.Problem);
    }

    [Fact]
    public void A_folder_of_pdfs_reads_the_same_way_a_folder_of_exports_does()
    {
        SparkasseStyle("august.pdf");

        var reading = Statement.Read(_root);

        var source = Assert.Single(reading.Sources);
        Assert.True(source.IsUnderstood);
        Assert.Equal(3, source.Charges);
        Assert.Equal(3, reading.Charges.Count);
    }

    [Fact]
    public void The_same_month_as_a_pdf_and_as_a_csv_is_counted_once()
    {
        // Somebody who finds the CSV export later should not end up with everything doubled.
        SparkasseStyle("august.pdf");
        File.WriteAllText(Path.Combine(_root, "august.csv"),
            "Buchungstag;Beguenstigter;Betrag\n01.09.2026;SEPA-LASTSCHRIFT FITNESS NORD GMBH;-29,90\n");

        var reading = Statement.Read(_root);

        Assert.Equal(1, reading.Duplicates);
        Assert.Equal(3, reading.Charges.Count);
    }

    [Fact]
    public void Nothing_in_the_folder_is_written_to()
    {
        // Same promise as for the exports, and a PDF library is a larger thing to take on trust.
        SparkasseStyle("august.pdf");
        var before = Directory.GetFiles(_root).Select(f => (f, new FileInfo(f).LastWriteTimeUtc)).ToList();

        Statement.Read(_root);

        Assert.Equal(before, Directory.GetFiles(_root).Select(f => (f, new FileInfo(f).LastWriteTimeUtc)).ToList());
    }
}

/// <summary>
/// The other shape a statement comes in: an online banking "Umsätze" print, which is not a table
/// at all. Each payment is three stacked lines, the payee, the amount alone at the right margin,
/// then the details, and no row begins with a date.
/// </summary>
public sealed class TinStackedPdfTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tin-stack-" + Guid.NewGuid().ToString("N")[..10]);

    public TinStackedPdfTests() => Directory.CreateDirectory(_root);

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

    private readonly record struct Ink(double X, double Y, string Text);

    private string Pdf(string name, params Ink[] ink)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(595, 842);

        foreach (var piece in ink)
            page.AddText(piece.Text, 9, new PdfPoint(piece.X, piece.Y), font);

        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    /// <summary>
    /// Money as a statement prints it: right aligned against the margin, so 9,00 and 1.755,99
    /// begin in quite different places and end in the same one. Roughly five points a digit at
    /// nine point Helvetica, which is close enough to sit in one column.
    /// </summary>
    private static Ink Money(double y, string amount) => new(508 - amount.Length * 5.0, y, amount);

    /// <summary>
    /// Payee, amount, details. The date exists nowhere except inside the details, stamped there
    /// by the card terminal.
    /// </summary>
    private static IEnumerable<Ink> Block(double y, string payee, string amount, string details) =>
    [
        new Ink(100, y, payee),
        Money(y - 14, amount),
        new Ink(511, y - 14, "EUR"),
        new Ink(100, y - 28, details),
    ];

    private string Umsaetze(string name = "umsaetze.pdf") => Pdf(name,
        [
            new Ink(67, 810, "Umsaetze"),

            // The account heading carries a balance, which is a number in the wrong column and
            // must not become the largest payment on the statement.
            new Ink(99, 795, "DE49 4265 0150 0001 2740 42"),
            new Ink(447, 795, "1.563,82"),
            new Ink(499, 795, "EUR"),

            .. Block(760, "NETTO MARKEN-DISCOU/IM HARSEWINKEL", "-23,52", "2026-09-09T16:26 Debitk.3 2026-12"),
            .. Block(700, "ERGO Krankenversicherung AG", "-30,60", "M101024003461 KRANKEN 01.09.26 Wir sagen Danke"),
            .. Block(640, "Renten Service", "1.755,99", "Rente 09/2026"),
            .. Block(580, "NETTO MARKEN-DISCOU/IM HARSEWINKEL", "-44,69", "2026-09-05T11:31 Debitk.3 2026-12"),
        ]);

    [Fact]
    public void A_payment_is_read_from_the_three_lines_it_is_stacked_across()
    {
        var reading = PdfStatement.Read(Umsaetze(), new DateTime(2026, 9, 10));

        Assert.Null(reading.Problem);
        Assert.Equal(4, reading.Charges.Count);

        var netto = reading.Charges.First(c => c.Name.StartsWith("NETTO", StringComparison.Ordinal));
        Assert.Equal(-23.52m, netto.Amount);

        // The date is in none of the three lines as a date. It is a timestamp inside the details.
        Assert.Equal(new DateTime(2026, 9, 9), netto.Date);
    }

    [Fact]
    public void An_amount_with_no_minus_is_money_coming_in_when_the_file_uses_minus_signs()
    {
        var reading = PdfStatement.Read(Umsaetze(), new DateTime(2026, 9, 10));

        Assert.Equal(1755.99m, reading.Charges.Single(c => c.Name.StartsWith("Renten", StringComparison.Ordinal)).Amount);
    }

    [Fact]
    public void The_balance_in_the_account_heading_is_not_a_payment()
    {
        // It is a number at the right hand side of the page like every amount is, and the only
        // thing separating it from one is that it sits in a different column.
        var reading = PdfStatement.Read(Umsaetze(), new DateTime(2026, 9, 10));

        Assert.DoesNotContain(reading.Charges, c => Math.Abs(c.Amount) == 1563.82m);
    }

    [Fact]
    public void A_day_heading_never_overrules_what_the_payment_says_about_itself()
    {
        // Found the hard way on a real statement: forty pages carrying five headings, and the
        // first one silently dated half a year of payments to the same day.
        var path = Pdf("stale.pdf",
        [
            new Ink(67, 800, "01.08.2026"),
            .. Block(760, "NETTO MARKEN-DISCOU/IM HARSEWINKEL", "-23,52", "2026-09-09T16:26 Debitk.3"),
            .. Block(700, "NETTO MARKEN-DISCOU/IM HARSEWINKEL", "-44,69", "2026-09-05T11:31 Debitk.3"),
            .. Block(640, "NETTO MARKEN-DISCOU/IM HARSEWINKEL", "-52,53", "2026-09-03T15:41 Debitk.3"),
        ]);

        var reading = PdfStatement.Read(path, new DateTime(2026, 9, 10));

        Assert.Equal(3, reading.Charges.Count);
        Assert.All(reading.Charges, c => Assert.Equal(9, c.Date.Month));
    }

    [Fact]
    public void A_heading_still_dates_a_payment_that_says_nothing_about_itself()
    {
        var path = Pdf("heading.pdf",
        [
            new Ink(67, 800, "04.08.2026"),
            .. Block(760, "Jutta Boehmer", "-200,00", "Sparen"),
            .. Block(700, "Robin Lindemann", "-106,12", "Miete"),
            .. Block(640, "STADTWERKE NORD", "-84,00", "Abschlag"),
        ]);

        var reading = PdfStatement.Read(path, new DateTime(2026, 9, 10));

        Assert.Equal(3, reading.Charges.Count);
        Assert.All(reading.Charges, c => Assert.Equal(new DateTime(2026, 8, 4), c.Date));
    }

    [Fact]
    public void The_details_under_a_payment_become_its_reference()
    {
        var reading = PdfStatement.Read(Umsaetze(), new DateTime(2026, 9, 10));

        var ergo = reading.Charges.Single(c => c.Name.StartsWith("ERGO", StringComparison.Ordinal));

        Assert.Equal("M101024003461 KRANKEN 01.09.26 Wir sagen Danke", ergo.Reference);

        // The line below the details belongs to the next payment, not to this one.
        Assert.DoesNotContain("Renten", ergo.Reference, StringComparison.Ordinal);
    }

    [Fact]
    public void An_amount_sharing_a_line_with_its_own_details_is_still_a_payment()
    {
        // Requiring the amount be alone on its line dropped one payment in four out of a real
        // statement, because a long reference sometimes shares the baseline with it.
        var path = Pdf("shared.pdf",
        [
            .. Block(780, "SWK ENERGIE GmbH", "-201,00", "Abschlag August"),
            .. Block(720, "SWK ENERGIE GmbH", "-233,00", "Abschlag Juli"),
            .. Block(660, "SWK ENERGIE GmbH", "-233,00", "Abschlag Juni"),

            new Ink(100, 600, "Sparkasse Vest Recklinghausen"),
            new Ink(100, 586, "Rechnung Darl.-Leistung 6000306321 Fuer 01.08.2026 - 30.08.2026 Tilgung"),
            Money(586, "-468,00"),
            new Ink(511, 586, "EUR"),
        ]);

        var reading = PdfStatement.Read(path, new DateTime(2026, 9, 10));

        Assert.Contains(reading.Charges, c => c.Amount == -468.00m && c.Name.Contains("Sparkasse", StringComparison.Ordinal));
    }
}
