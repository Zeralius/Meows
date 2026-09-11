using System.Text;

namespace Meows.Plugins.Tin.Services;

/// <summary>
/// Finding which account a file is about.
///
/// Every export says so somewhere, and never in the same place twice: a Sparkasse CSV puts it in
/// an Auftragskonto column, a PDF prints it in the heading, split into groups of four. Rather than
/// know where each bank writes it, this looks everywhere and lets the checksum decide.
///
/// The checksum is what makes that safe. A statement is full of long digit strings — contract
/// numbers, mandate references, customer numbers — and without the mod 97 test half of them would
/// read as accounts.
/// </summary>
public static class Iban
{
    /// <summary>Not an account, but somewhere to put the files that never said.</summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// The first account named anywhere in these lines.
    ///
    /// The first rather than the most common: an export names its own account in the heading and
    /// then names everybody else's in the rows underneath, so the earliest one is the one the file
    /// is about.
    /// </summary>
    public static string? Find(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (In(line) is { } found)
                return found;
        }

        return null;
    }

    /// <summary>
    /// An account in one line of text.
    ///
    /// Spaces are dropped before looking, because a PDF prints DE49 4265 0150 0001 2740 42 as six
    /// separate words and a CSV prints it as one. That also means the joined text of a line can
    /// form an account that was never written there, which is exactly what the checksum catches.
    /// </summary>
    public static string? In(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var tight = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
                tight.Append(char.ToUpperInvariant(c));
        }

        var run = tight.ToString();

        for (var start = 0; start + 15 <= run.Length; start++)
        {
            if (!char.IsLetter(run[start]) || !char.IsLetter(run[start + 1]))
                continue;

            if (!char.IsDigit(run[start + 2]) || !char.IsDigit(run[start + 3]))
                continue;

            // A country's accounts are all one length, so where the country is known there is
            // exactly one candidate to test. That matters more than it sounds: a CSV row runs the
            // account straight into a date, and the checksum passes one long candidate in every
            // ninety seven by luck, which is often enough to matter over a folder of statements.
            if (Length(run.Substring(start, 2)) is { } known)
            {
                if (start + known > run.Length)
                    continue;

                var exact = run.Substring(start, known);
                if (IsReal(exact))
                    return exact;

                continue;
            }

            // A country nobody here has heard of. Longest first, and take what validates.
            for (var length = Math.Min(34, run.Length - start); length >= 15; length--)
            {
                var candidate = run.Substring(start, length);
                if (IsReal(candidate))
                    return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// How long an account is in a given country. Not every country there is, but every country a
    /// German bank statement is likely to name, and anything missing simply falls back to trying
    /// every length.
    /// </summary>
    private static int? Length(string country) => country switch
    {
        "NO" => 15,
        "BE" => 16,
        "DK" or "FI" or "FO" or "GL" or "NL" => 18,
        "MK" or "SI" => 19,
        "AT" or "BA" or "EE" or "LT" or "LU" or "LV" => 20,
        "CH" or "HR" or "LI" => 21,
        "BG" or "DE" or "GB" or "IE" => 22,
        "GI" or "IL" => 23,
        "AD" or "CZ" or "ES" or "PK" or "RO" or "SA" or "SE" or "SK" or "TN" or "VG" => 24,
        "PT" => 25,
        "IS" or "TR" => 26,
        "FR" or "GR" or "IT" or "MC" or "MR" or "SM" => 27,
        "AL" or "AZ" or "CY" or "DO" or "GE" or "HU" or "LB" or "PL" => 28,
        "BR" or "QA" or "UA" => 29,
        "MT" => 31,
        _ => null,
    };

    /// <summary>
    /// The mod 97 check every IBAN carries: move the first four characters to the end, turn the
    /// letters into numbers, and the whole thing divided by 97 leaves 1.
    /// </summary>
    public static bool IsReal(string? iban)
    {
        if (string.IsNullOrWhiteSpace(iban) || iban.Length is < 15 or > 34)
            return false;

        var moved = iban[4..] + iban[..4];
        var remainder = 0;

        foreach (var c in moved)
        {
            if (char.IsDigit(c))
            {
                remainder = (remainder * 10 + (c - '0')) % 97;
            }
            else if (char.IsAsciiLetterUpper(c))
            {
                // A becomes 10, Z becomes 35, and both digits go through the running remainder.
                var value = c - 'A' + 10;
                remainder = (remainder * 100 + value) % 97;
            }
            else
            {
                return false;
            }
        }

        return remainder == 1;
    }

    /// <summary>
    /// Enough of an account to recognise it, and not enough to write down. Nobody needs the middle
    /// of their own IBAN to know which of two accounts they are looking at.
    /// </summary>
    public static string Mask(string? iban)
    {
        if (string.IsNullOrWhiteSpace(iban) || iban == Unknown)
            return "";

        return iban.Length <= 8 ? iban : $"{iban[..4]} … {iban[^4..]}";
    }
}
