namespace Meows.Plugins.Tin.Services;

/// <summary>One wedge: a payee, what they took, and what share of everything that was.</summary>
public sealed record Slice(string Name, decimal Total, double Share, bool IsRest);

/// <summary>What is left over once the regular payments are taken out of a month.</summary>
public sealed record Rhythm(decimal Known, decimal Typical, int Months)
{
    public decimal Total => Known + Typical;

    /// <summary>Two complete months is the least that can be called typical of anything.</summary>
    public bool IsUseful => Months >= 2;
}

/// <summary>
/// Where the money went, and how much of it is likely to go again.
///
/// Everything here is arithmetic on charges that have already been read. Nothing predicts: the
/// forecast is what the last few months did, split into the part that repeats on a known rhythm
/// and the part that does not, because those two behave differently and adding them into one
/// average hides which is which.
/// </summary>
public static class Spending
{
    /// <summary>Wedges worth drawing before the rest of them stop being legible.</summary>
    public const int Wedges = 8;

    /// <summary>
    /// Outgoing money grouped by who took it, biggest first, with everything past the eighth
    /// gathered into one wedge.
    ///
    /// Grouped by company rather than by subscription: an insurer taking three policies is one
    /// wedge here and three rows in the list, because the question a chart answers is where the
    /// money went rather than what it was for.
    /// </summary>
    public static IReadOnlyList<Slice> ByPayee(IEnumerable<Charge> charges, int wedges = Wedges)
    {
        var going = charges.Where(c => c.Amount < 0).ToList();
        var total = going.Sum(c => Math.Abs(c.Amount));

        if (total <= 0)
            return [];

        var grouped = going
            .GroupBy(c => Recurring.KeyOf(c.Name, Recurring.Company), StringComparer.Ordinal)
            .Select(g => (Name: Recurring.NameOf(g.OrderBy(c => c.Date).Last().Name),
                Total: g.Sum(c => Math.Abs(c.Amount))))
            .OrderByDescending(g => g.Total)
            .ToList();

        var slices = grouped
            .Take(wedges)
            .Select(g => new Slice(g.Name, g.Total, (double)(g.Total / total), false))
            .ToList();

        var rest = grouped.Skip(wedges).Sum(g => g.Total);
        if (rest > 0)
            slices.Add(new Slice("", rest, (double)(rest / total), true));

        return slices;
    }

    /// <summary>
    /// How a month usually goes, in one direction.
    ///
    /// Two halves, kept apart on purpose. <b>Known</b> is what repeats on a rhythm Tin recognised,
    /// projected forward from its own cadence rather than from an average. <b>Typical</b> is
    /// everything else — the shopping, the odd repair — taken as the middle month rather than the
    /// mean, so one enormous month does not raise the estimate of every month after it.
    ///
    /// Only whole months count. The first and last months of a folder of exports are nearly always
    /// partial, and a half month read as a whole one drags the answer down.
    /// </summary>
    public static Rhythm Monthly(IReadOnlyList<Charge> charges, DateTime now, bool incoming,
        Func<Charge, string>? accountOf = null)
    {
        var wanted = charges.Where(c => incoming ? c.Amount > 0 : c.Amount < 0).ToList();
        if (wanted.Count == 0)
            return new Rhythm(0, 0, 0);

        if (accountOf is not null)
            wanted = WithoutInternalTransfers(wanted, accountOf);
        if (wanted.Count == 0)
            return new Rhythm(0, 0, 0);

        var whole = WholeMonths(wanted);
        if (whole.Count == 0)
            return new Rhythm(0, 0, 0);

        var series = Recurring.Find(wanted, now, incoming);
        var known = series.Sum(s => s.Ongoing);

        // Whatever is not part of a series it recognised. Grouped by month so the middle month can
        // be picked, which is the whole point of not using the mean here.
        var accounted = series.SelectMany(s => s.Charges).ToHashSet();

        var leftovers = whole
            .Select(month => wanted
                .Where(c => !accounted.Contains(c))
                .Where(c => c.Date.Year == month.Year && c.Date.Month == month.Month)
                .Sum(c => Math.Abs(c.Amount)))
            .OrderBy(total => total)
            .ToList();

        // The lower middle, so two months read as the quieter of the two rather than the louder.
        // A single large one-off next to an ordinary month must not become the typical month.
        return new Rhythm(known, leftovers[(leftovers.Count - 1) / 2], whole.Count);
    }

    /// <summary>
    /// Money moved between the user's own accounts, set aside before either direction is
    /// totalled. The same amount leaving one account and arriving in another within a couple
    /// of days is not spending and not income; counted, it inflates the month coming in and
    /// going out by the move. Only matched when the two sides sit in different accounts, so a
    /// folder of one account behaves exactly as before, and only on exact amounts, so a wage
    /// near a transfer does not get eaten.
    /// </summary>
    public static List<Charge> WithoutInternalTransfers(
        IReadOnlyList<Charge> wanted, Func<Charge, string> accountOf)
    {
        const int WindowDays = 2;

        var matched = new bool[wanted.Count];

        foreach (var group in wanted
                     .Select((c, i) => (Charge: c, Index: i))
                     .GroupBy(t => Math.Abs(t.Charge.Amount)))
        {
            var outs = group.Where(t => t.Charge.Amount < 0).OrderBy(t => t.Charge.Date).ToList();
            var ins = group.Where(t => t.Charge.Amount > 0).OrderBy(t => t.Charge.Date).ToList();
            if (outs.Count == 0 || ins.Count == 0)
                continue;

            foreach (var (outCharge, outIndex) in outs)
            {
                if (matched[outIndex])
                    continue;

                var outAccount = accountOf(outCharge);
                if (string.IsNullOrEmpty(outAccount) || outAccount == Iban.Unknown)
                    continue;

                foreach (var (inCharge, inIndex) in ins)
                {
                    if (matched[inIndex])
                        continue;

                    var inAccount = accountOf(inCharge);
                    if (inAccount == outAccount || string.IsNullOrEmpty(inAccount) || inAccount == Iban.Unknown)
                        continue;

                    if (Math.Abs((inCharge.Date - outCharge.Date).TotalDays) > WindowDays)
                        continue;

                    matched[outIndex] = true;
                    matched[inIndex] = true;
                    break;
                }
            }
        }

        return wanted.Where((_, i) => !matched[i]).ToList();
    }

    /// <summary>
    /// A year of it. The rhythms are counted at their own cadence rather than multiplied by
    /// twelve, so a yearly insurance premium counts once and a quarterly bill four times.
    /// </summary>
    public static Rhythm Yearly(IReadOnlyList<Charge> charges, DateTime now, bool incoming)
    {
        var monthly = Monthly(charges, now, incoming);
        return monthly with { Known = monthly.Known * 12m, Typical = monthly.Typical * 12m };
    }

    /// <summary>
    /// The months a folder covers completely.
    ///
    /// An export starting on the fifteenth makes that month a half month, and the month a folder
    /// was downloaded in is nearly always unfinished, so both ends are dropped.
    /// </summary>
    public static IReadOnlyList<DateTime> WholeMonths(IReadOnlyList<Charge> charges)
    {
        if (charges.Count == 0)
            return [];

        var first = charges.Min(c => c.Date);
        var last = charges.Max(c => c.Date);

        // The first whole month is the one after the month the exports begin in, unless they begin
        // on its first day.
        var from = first.Day == 1
            ? new DateTime(first.Year, first.Month, 1)
            : new DateTime(first.Year, first.Month, 1).AddMonths(1);

        // The last whole month is the one before the month they end in, unless they run to its
        // final day.
        var end = new DateTime(last.Year, last.Month, 1);
        var to = last.Day == DateTime.DaysInMonth(last.Year, last.Month) ? end : end.AddMonths(-1);

        var months = new List<DateTime>();
        for (var month = from; month <= to; month = month.AddMonths(1))
            months.Add(month);

        return months;
    }
}
