using System.Globalization;

namespace Meows.Plugins.Tin.Services;

/// <summary>
/// Reading the two fields every bank writes differently: what a row cost and when it happened.
///
/// Nothing here uses the current culture. A German export opened on an English Windows has to
/// read the same as it does at home, and <c>CultureInfo.CurrentCulture</c> would make the answer
/// depend on the machine rather than on the file.
/// </summary>
public static class Money
{
    private static readonly string[] DateFormats =
    [
        // European first, deliberately. dd/MM and MM/dd cannot be told apart on the twelfth of
        // the month or earlier, and every export this was written for is European.
        "dd.MM.yyyy", "d.M.yyyy", "dd.MM.yy", "d.M.yy",
        "yyyy-MM-dd", "yyyy/MM/dd", "yyyyMMdd",
        "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy",
        "MM/dd/yyyy", "M/d/yyyy",
        "dd.MM.yyyy HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss",
    ];

    public static bool TryDate(string? text, out DateTime date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();

        if (DateTime.TryParseExact(trimmed, DateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var exact))
        {
            date = exact.Date;
            return true;
        }

        // A last try for anything ISO-ish that the list above does not spell out.
        if (DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var loose))
        {
            date = loose.Date;
            return true;
        }

        return false;
    }

    /// <summary>
    /// An amount, however it was written. Negative means it left the account.
    ///
    /// The separator question is settled by counting digits rather than by guessing a country: a
    /// grouping separator always has exactly three digits behind it, so anything else is the
    /// decimal point. That reads 1.234,56 and 1,234.56 and 12,34 and 12.34 correctly without
    /// being told which bank wrote them.
    /// </summary>
    public static bool TryAmount(string? text, out decimal amount)
    {
        amount = 0m;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var raw = text.Trim();
        var negative = false;

        // (12,34) is a debit in exports that came out of a spreadsheet.
        if (raw.StartsWith('(') && raw.EndsWith(')'))
        {
            negative = true;
            raw = raw[1..^1];
        }

        // 12,34- is a debit in exports that came off a mainframe.
        if (raw.EndsWith('-'))
        {
            negative = true;
            raw = raw[..^1];
        }

        var digits = new System.Text.StringBuilder();
        foreach (var c in raw)
        {
            if (char.IsDigit(c) || c is '.' or ',')
                digits.Append(c);
            else if (c is '-')
                negative = true;
            else if (c is '+' || char.IsWhiteSpace(c) || c == '\u00A0')
                continue;

            // Everything else is a currency symbol, a code or a stray letter, and none of those
            // change the number.
        }

        var cleaned = digits.ToString();
        if (cleaned.Length == 0)
            return false;

        var lastDot = cleaned.LastIndexOf('.');
        var lastComma = cleaned.LastIndexOf(',');
        var separator = Math.Max(lastDot, lastComma);

        if (separator >= 0)
        {
            var behind = cleaned.Length - separator - 1;
            if (behind == 3)
            {
                // Three digits behind it: grouping, not a decimal point. 1.234 is one thousand
                // two hundred and thirty four, not one and a bit.
                cleaned = cleaned.Replace(".", "").Replace(",", "");
            }
            else
            {
                var whole = cleaned[..separator].Replace(".", "").Replace(",", "");
                var fraction = cleaned[(separator + 1)..].Replace(".", "").Replace(",", "");
                cleaned = fraction.Length == 0 ? whole : whole + "." + fraction;
            }
        }

        if (!decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            return false;

        amount = negative ? -Math.Abs(value) : value;
        return true;
    }

    /// <summary>
    /// Money as it is shown. The currency is whatever the settings say, because a CSV rarely
    /// says and guessing one is worse than being told, and the culture comes from the language
    /// the window is in rather than from the machine.
    /// </summary>
    public static string Show(decimal amount, string currency, CultureInfo culture) =>
        amount.ToString("N2", culture) + " " + currency;
}
