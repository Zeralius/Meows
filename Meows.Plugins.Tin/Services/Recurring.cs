namespace Meows.Plugins.Tin.Services;

/// <summary>How often something comes round.</summary>
public enum Cadence
{
    Weekly,
    Fortnightly,
    Monthly,
    Quarterly,
    HalfYearly,
    Yearly,
}

/// <summary>
/// One thing that keeps going out: the same payee, at a steady interval, more than twice.
/// </summary>
public sealed record Series(
    string Key,
    string Name,
    Cadence Cadence,
    IReadOnlyList<Charge> Charges,
    decimal LastAmount,
    decimal? PreviousAmount,
    DateTime Last,
    DateTime Due,
    bool Lapsed,
    string Reference = "")
{
    /// <summary>What it costs a month, whatever its actual rhythm, so a total can be added up.</summary>
    public decimal PerMonth => Math.Abs(LastAmount) * Recurring.TimesPerYear(Cadence) / 12m;

    /// <summary>
    /// What it costs a month going forward, which is nothing once it has stopped. Totals add this
    /// rather than <see cref="PerMonth"/>, because a cancelled gym is still worth listing and is
    /// not worth paying for.
    /// </summary>
    public decimal Ongoing => Lapsed ? 0m : PerMonth;

    /// <summary>
    /// A rise worth pointing at. Rounding noise and a cent of exchange rate are not news, so a
    /// change has to be at least one percent and at least half a unit of currency.
    /// </summary>
    public bool WentUp => PreviousAmount is { } previous
                          && Math.Abs(LastAmount) > Math.Abs(previous)
                          && Math.Abs(LastAmount) - Math.Abs(previous) >= 0.5m
                          && Math.Abs(LastAmount) >= Math.Abs(previous) * 1.01m;

    public decimal Rise => PreviousAmount is { } previous ? Math.Abs(LastAmount) - Math.Abs(previous) : 0m;
}

/// <summary>
/// Finds what repeats.
///
/// Only money going out. Wages and refunds repeat too, but the question this plugin was written
/// for is what leaves the account every month without being asked about again.
/// </summary>
public static class Recurring
{
    /// <summary>Three charges, so there are two gaps to compare. Two charges is a coincidence.</summary>
    public const int Fewest = 3;

    /// <summary>
    /// What repeats.
    ///
    /// Outgoing by default, because that is the question the tab exists to answer. Incoming is
    /// asked for separately and only by the forecast, where a wage or a pension arriving every
    /// month is exactly as much a rhythm as a subscription leaving.
    /// </summary>
    public static IReadOnlyList<Series> Find(IEnumerable<Charge> charges, DateTime now, bool incoming = false)
    {
        var found = new List<Series>();

        var wanted = charges.Where(c => incoming ? c.Amount > 0 : c.Amount < 0).ToList();
        var keys = Canonical(wanted.Select(c => KeyOf(c.Name)));

        var groups = wanted
            .GroupBy(c => keys[KeyOf(c.Name)], StringComparer.Ordinal)
            .Where(g => g.Key.Length > 0);

        foreach (var group in groups)
        {
            var ordered = group.OrderBy(c => c.Date).ToList();
            if (ordered.Count < Fewest)
                continue;

            var chains = Chains(ordered);

            // Several things billed under one name: each chain is its own subscription.
            if (chains.Count >= 2)
            {
                foreach (var chain in chains)
                {
                    if (Woven($"{group.Key}#{chain[0].Amount}", chain, now) is { } series)
                        found.Add(series);
                }

                continue;
            }

            // One thing. The whole group first, so a price that changed after only two months
            // still counts as a rise rather than being left out; failing that, the one steady
            // chain in it, because a stray one-off from the same payee — a refund, a correction —
            // must not make a quarterly bill look like no rhythm at all.
            if (Woven(group.Key, ordered, now) is { } whole)
                found.Add(whole);
            else if (chains.Count == 1 && Woven(group.Key, chains[0], now) is { } only)
                found.Add(only);
            else if (Core(ordered) is { } core && Woven(group.Key, core, now) is { } most)
                found.Add(most);
        }

        return found.OrderByDescending(s => s.PerMonth).ToList();
    }

    /// <summary>
    /// One payee, however it spelled itself that month.
    ///
    /// A hosting company writes STRATO on some rows and STRATO with its street on others, and read
    /// as two payees each has half the months and one of them looks cancelled. A key that begins
    /// with another key, word for word, is the same payee writing more, so it is folded into the
    /// shorter one. The shortest, if there are several: STRATO OTTO OSTROWSKI folds into STRATO
    /// OTTO folds into STRATO.
    /// </summary>
    private static Dictionary<string, string> Canonical(IEnumerable<string> keys)
    {
        var distinct = keys.Where(k => k.Length > 0).Distinct(StringComparer.Ordinal)
            .OrderBy(k => k.Length)
            .ToList();

        var canonical = new Dictionary<string, string>(StringComparer.Ordinal) { [""] = "" };

        foreach (var key in distinct)
        {
            var shortest = key;

            foreach (var other in distinct)
            {
                if (other.Length >= shortest.Length)
                    break;

                if (key.StartsWith(other + " ", StringComparison.Ordinal))
                {
                    shortest = other;
                    break;
                }
            }

            canonical[key] = shortest;
        }

        return canonical;
    }

    /// <summary>
    /// One payee is sometimes several things.
    ///
    /// An insurer takes four policies from the same account on the same day every month, and read
    /// as one payee that is not a rhythm at all: four payments land together, then nothing for a
    /// month, and the gaps between them say zero, zero, zero, thirty. Read as four, each is
    /// plainly monthly.
    ///
    /// The amounts are what tell them apart, so each steady amount is a run. But a policy that went
    /// from 86,08 to 90,50 in January is two runs and one policy, so a run that begins where
    /// another ended, one interval later, is chained onto it: the same thing at a new price. What
    /// is left is one chain per thing being billed, each with its own rises inside it.
    /// </summary>
    private static List<List<Charge>> Chains(List<Charge> ordered)
    {
        var runs = Runs(ordered)
            .Where(r => r.Count >= Fewest && CadenceOf(Gaps(r)) is not null)
            .OrderBy(r => r[0].Date)
            .ToList();

        var chains = new List<List<Charge>>();
        var taken = new bool[runs.Count];

        for (var i = 0; i < runs.Count; i++)
        {
            if (taken[i])
                continue;

            var chain = new List<Charge>(runs[i]);
            taken[i] = true;

            // Keep following: the run that starts soonest after this chain ends and costs the
            // nearest amount is the same thing at a new price. Nearest amount decides, because
            // four policies rising in the same January all start new runs on the same day and
            // only the price says which continues which.
            while (Follower(chain, runs, taken) is { } next)
            {
                chain.AddRange(runs[next]);
                taken[next] = true;
            }

            chains.Add(chain);
        }

        return chains;
    }

    /// <summary>
    /// The charges grouped by what they cost, with a little give.
    ///
    /// An energy company's monthly instalment is 232,00 one month and 233,00 the next, and read as
    /// two different amounts that is two overlapping subscriptions rather than one. The give is
    /// the same as the rule for what counts as a price rise, so that anything too small to be
    /// called a rise is also too small to be called a different thing.
    /// </summary>
    private static List<List<Charge>> Runs(List<Charge> ordered)
    {
        var runs = new List<List<Charge>>();

        foreach (var charge in ordered.OrderBy(c => Math.Abs(c.Amount)))
        {
            var run = runs.LastOrDefault();

            if (run is not null && Alike(run[^1].Amount, charge.Amount))
                run.Add(charge);
            else
                runs.Add([charge]);
        }

        foreach (var run in runs)
            run.Sort((a, b) => a.Date.CompareTo(b.Date));

        return runs;
    }

    /// <summary>Too close to be called a different price. The mirror of what counts as a rise.</summary>
    public static bool Alike(decimal a, decimal b)
    {
        var difference = Math.Abs(Math.Abs(a) - Math.Abs(b));
        return difference < 0.5m || difference < Math.Max(Math.Abs(a), Math.Abs(b)) * 0.01m;
    }

    private static int? Follower(List<Charge> chain, List<List<Charge>> runs, bool[] taken)
    {
        var last = chain[^1];
        var cadence = CadenceOf(Gaps(chain)) ?? Cadence.Monthly;

        // One interval, with room for a late month. Two intervals later is something new.
        var limit = Days(cadence) * 1.6;

        int? best = null;
        var nearest = decimal.MaxValue;

        for (var j = 0; j < runs.Count; j++)
        {
            if (taken[j])
                continue;

            var gap = (runs[j][0].Date - last.Date).TotalDays;
            if (gap <= 0 || gap > limit)
                continue;

            var difference = Math.Abs(Math.Abs(runs[j][0].Amount) - Math.Abs(last.Amount));
            if (difference < nearest)
            {
                nearest = difference;
                best = j;
            }
        }

        return best;
    }

    private static double Days(Cadence cadence) => cadence switch
    {
        Cadence.Weekly => 7,
        Cadence.Fortnightly => 14,
        Cadence.Monthly => 30.4,
        Cadence.Quarterly => 91,
        Cadence.HalfYearly => 182,
        _ => 365,
    };

    /// <summary>
    /// The group with its strays taken out, or nothing if there were none to take.
    ///
    /// The city takes a property tax instalment of three hundred every quarter, and once, at a
    /// counter, fifty three by card. Read together the fifty three lands between two instalments
    /// and there is no rhythm left. A charge an order of magnitude off what the payee usually
    /// takes is a different transaction that happens to carry the same name.
    ///
    /// A last resort, after the whole group and its chains have failed, because used earlier it
    /// would strip the largest of an insurer's four policies for being three times the middle one.
    /// </summary>
    private static List<Charge>? Core(List<Charge> ordered)
    {
        var sizes = ordered.Select(c => Math.Abs(c.Amount)).OrderBy(a => a).ToList();
        var middle = sizes[sizes.Count / 2];

        var core = ordered
            .Where(c => Math.Abs(c.Amount) >= middle / 3m && Math.Abs(c.Amount) <= middle * 3m)
            .ToList();

        return core.Count < ordered.Count && core.Count >= Fewest ? core : null;
    }

    private static List<int> Gaps(List<Charge> ordered)
    {
        var gaps = new List<int>();

        for (var i = 1; i < ordered.Count; i++)
            gaps.Add((int)(ordered[i].Date - ordered[i - 1].Date).TotalDays);

        return gaps;
    }

    /// <summary>One list of charges as a series, or nothing if they keep no rhythm.</summary>
    private static Series? Woven(string key, List<Charge> ordered, DateTime now)
    {
        if (ordered.Count < Fewest || CadenceOf(Gaps(ordered)) is not { } cadence)
            return null;

        var last = ordered[^1];

        // The last amount that was not the current one, which is what "it went up" compares
        // against. A run of identical charges has none, and that is not a price change. And a
        // price it was charged exactly once is not a price it used to be: a one-off correction
        // of 2,67 in January does not make the premium look like it rose by fifty.
        var earlier = ordered.Take(ordered.Count - 1).Select(c => c.Amount).ToList();

        var previous = earlier
            .Where(a => !Alike(a, last.Amount))
            .Where(a => earlier.Count(other => Alike(other, a)) >= 2)
            .Select(a => (decimal?)a)
            .LastOrDefault()
            ?? earlier
                .Where(a => !Alike(a, last.Amount))
                .Select(a => (decimal?)a)
                .LastOrDefault();

        var due = Next(last.Date, cadence);

        return new Series(
            key,
            // The most recent spelling of the name, tidied. Payees rename themselves, so the
            // current name is the one that will be recognised.
            NameOf(last.Name),
            cadence,
            ordered,
            last.Amount,
            previous,
            last.Date,
            due,
            now.Date > due.AddDays(Grace(cadence)),
            // The newest reference rather than a merged one. They differ month to month, and the
            // most recent is the one that matches what the bank is showing right now.
            last.Reference);
    }

    /// <summary>
    /// The name with everything that changes month to month taken out of it: reference numbers,
    /// dates, card digits, the mandate id. What is left is the bit that stays the same, which is
    /// the only part worth grouping on.
    /// </summary>
    /// <summary>How many words make a name, when the question is which subscription this is.</summary>
    public const int Words = 3;

    /// <summary>
    /// How many words make a name, when the question is which company this is.
    ///
    /// Coarser on purpose. A payee writes its own name differently from one statement to the next
    /// — the same energy company appears with its street on some rows and without it on others —
    /// and for a chart of where the money went those are one company. For the list of
    /// subscriptions they are not: the finer key is what keeps a company's two contracts apart.
    /// </summary>
    public const int Company = 2;

    public static string KeyOf(string? name, int words = Words)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";

        var found = new List<string>();
        var word = new System.Text.StringBuilder();

        foreach (var c in name.ToUpperInvariant() + " ")
        {
            if (char.IsLetter(c))
            {
                word.Append(c);
            }
            else if (char.IsDigit(c))
            {
                // A word with a digit in it is a reference, not a name. Marking it rather than
                // dropping the digit keeps INVOICE2024 out of the key instead of turning it into
                // INVOICE, which would group January with a different invoice in June.
                word.Append('0');
            }
            else
            {
                if (word.Length > 0)
                    found.Add(word.ToString());
                word.Clear();
            }
        }

        var kept = found
            .Where(w => !w.Contains('0'))
            .Where(w => w.Length >= 3)
            .Where(w => !Noise.Contains(w))
            .Take(words)
            .ToList();

        return string.Join(" ", kept);
    }

    /// <summary>
    /// The name as it is worth showing: the same words the key is built from, but in the order
    /// and the casing the bank wrote them.
    ///
    /// Without this a row reads SEPA LASTSCHRIFT STADTWERKE NORD RE 20260801, where every part
    /// except two words is either how the money moved or a reference that will be different next
    /// month. The grouping already ignores all of that; the label may as well too.
    /// </summary>
    public static string NameOf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";

        var kept = name
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => !w.Any(char.IsDigit))
            .Where(w => !IsNoise(w))
            .Take(6)
            .ToList();

        // Everything was noise, which happens when the whole field is a reference. Better the
        // raw text than an empty row.
        var shown = kept.Count > 0 ? string.Join(" ", kept) : name.Trim();

        return shown.Length > 60 ? shown[..60] : shown;
    }

    /// <summary>
    /// A word carrying nothing worth reading.
    ///
    /// Judged by its parts rather than whole, because banks join them up: SEPA-LASTSCHRIFT is one
    /// token and two noise words, and matching the whole thing lets it straight through.
    /// </summary>
    private static bool IsNoise(string word)
    {
        var runs = new List<string>();
        var run = new System.Text.StringBuilder();

        foreach (var c in word.ToUpperInvariant() + " ")
        {
            if (char.IsLetter(c))
            {
                run.Append(c);
                continue;
            }

            if (run.Length > 0)
                runs.Add(run.ToString());

            run.Clear();
        }

        // Nothing but punctuation, or every part of it is either a filler word or too short to
        // be one at all.
        return runs.Count == 0 || runs.All(r => r.Length < 3 || Noise.Contains(r));
    }

    /// <summary>
    /// Words that say how the money moved rather than who took it. Left in, every direct debit in
    /// the account groups together under LASTSCHRIFT and the answer is one enormous subscription.
    /// </summary>
    private static readonly HashSet<string> Noise = new(StringComparer.Ordinal)
    {
        "LASTSCHRIFT", "SEPA", "DAUERAUFTRAG", "UEBERWEISUNG", "KARTENZAHLUNG", "BASISLASTSCHRIFT",
        "FOLGELASTSCHRIFT", "EINZUGSERMAECHTIGUNG", "MANDAT", "GLAEUBIGER", "REFERENZ",
        "VERWENDUNGSZWECK", "KUNDENREFERENZ", "ABBUCHUNG", "GEBUEHR", "ENTGELT", "RECHNUNG",
        "DIRECT", "DEBIT", "PAYMENT", "TRANSFER", "STANDING", "ORDER", "REFERENCE", "CARD",
        "PURCHASE", "TRANSACTION", "MANDATE", "INVOICE", "THE", "AND", "FOR", "VON", "DER",
        "DIE", "DAS", "GMBH", "LTD", "INC",
    };

    /// <summary>
    /// The rhythm the gaps agree on, or nothing. Two thirds of them have to land in the same
    /// bucket as the middle one: a subscription with one late month is still a subscription, and
    /// three unrelated payments to the same shop are not.
    /// </summary>
    public static Cadence? CadenceOf(IReadOnlyList<int> gaps)
    {
        if (gaps.Count < Fewest - 1)
            return null;

        var sorted = gaps.OrderBy(g => g).ToList();
        var median = sorted[sorted.Count / 2];

        if (Bucket(median) is not { } cadence)
            return null;

        var agreeing = gaps.Count(g => Bucket(g) == cadence);
        return agreeing * 3 >= gaps.Count * 2 ? cadence : null;
    }

    private static Cadence? Bucket(int days) => days switch
    {
        >= 5 and <= 9 => Cadence.Weekly,
        >= 11 and <= 18 => Cadence.Fortnightly,
        >= 25 and <= 38 => Cadence.Monthly,
        >= 80 and <= 100 => Cadence.Quarterly,
        >= 170 and <= 200 => Cadence.HalfYearly,
        >= 340 and <= 400 => Cadence.Yearly,
        _ => null,
    };

    /// <summary>
    /// When the next one is due. Months are added as months rather than as thirty days, so a
    /// subscription taken on the 31st stays at the end of the month.
    /// </summary>
    public static DateTime Next(DateTime from, Cadence cadence) => cadence switch
    {
        Cadence.Weekly => from.AddDays(7),
        Cadence.Fortnightly => from.AddDays(14),
        Cadence.Monthly => from.AddMonths(1),
        Cadence.Quarterly => from.AddMonths(3),
        Cadence.HalfYearly => from.AddMonths(6),
        _ => from.AddYears(1),
    };

    /// <summary>
    /// How late it has to be before it counts as stopped. Roughly a third of the interval,
    /// because a monthly charge landing four days late is a weekend, not a cancellation.
    /// </summary>
    public static int Grace(Cadence cadence) => cadence switch
    {
        Cadence.Weekly => 5,
        Cadence.Fortnightly => 6,
        Cadence.Monthly => 10,
        Cadence.Quarterly => 20,
        Cadence.HalfYearly => 30,
        _ => 45,
    };

    /// <summary>
    /// How many times a year it comes round. Kept as a whole number and divided by twelve at the
    /// end, because a factor of a twelfth is a repeating decimal and a yearly charge of eighteen
    /// then costs 1.4999999999 a month.
    /// </summary>
    public static decimal TimesPerYear(Cadence cadence) => cadence switch
    {
        Cadence.Weekly => 52m,
        Cadence.Fortnightly => 26m,
        Cadence.Monthly => 12m,
        Cadence.Quarterly => 4m,
        Cadence.HalfYearly => 2m,
        _ => 1m,
    };

    /// <summary>The key for a name rather than the name itself. Nothing here knows any language.</summary>
    public static string Describe(Cadence cadence) => "tin.cadence." + cadence.ToString().ToLowerInvariant();
}
