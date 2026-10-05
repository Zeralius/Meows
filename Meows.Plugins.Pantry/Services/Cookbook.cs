using Meows.Plugins.Abstractions;

namespace Meows.Plugins.Pantry.Services;

/// <summary>One recipe, as typed: a title, markdown body, minutes and tags. No parsing, ever.</summary>
public sealed class Recipe
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Title { get; set; } = "";

    public string Body { get; set; } = "";

    /// <summary>Zero means nobody timed it.</summary>
    public int Minutes { get; set; }

    public List<string> Tags { get; set; } = [];
}

/// <summary>Something in the fridge with a date on it.</summary>
public sealed class StockItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    /// <summary>The day it runs out. Time of day is never part of it.</summary>
    public DateTime Expires { get; set; } = DateTime.Today;

    public string Note { get; set; } = "";
}

/// <summary>One day's plan: this date, this recipe, or nothing yet.</summary>
public sealed class PlanSlot
{
    public DateTime Date { get; set; } = DateTime.Today;

    public string? RecipeId { get; set; }
}

/// <summary>
/// The arithmetic behind the pantry: what runs out, what the week says, and what tonight is.
///
/// Pure, and told what today is rather than reading the clock, so a test can sit on a date and a
/// pass that starts before midnight cannot answer differently to the one after it.
/// </summary>
public static class Cookbook
{
    /// <summary>Anything this far out or closer starts being worth saying out loud.</summary>
    public const int LeadDays = 3;

    /// <summary>How many days the plan shows, starting today.</summary>
    public const int PlanDays = 7;

    /// <summary>Past days fall off; the plan always starts today.</summary>
    public static void RollPlan(List<PlanSlot> plan, DateTime today)
    {
        plan.RemoveAll(s => s.Date.Date < today.Date);
        foreach (var day in NextDays(today))
            if (plan.All(s => s.Date.Date != day))
                plan.Add(new PlanSlot { Date = day });
        plan.Sort((a, b) => a.Date.CompareTo(b.Date));
    }

    public static IReadOnlyList<DateTime> NextDays(DateTime today) =>
        Enumerable.Range(0, PlanDays).Select(i => today.Date.AddDays(i)).ToList();

    public static List<StockItem> Expired(IEnumerable<StockItem> stock, DateTime today) =>
        stock.Where(s => s.Expires.Date < today.Date).OrderBy(s => s.Expires).ToList();

    public static List<StockItem> Soon(IEnumerable<StockItem> stock, DateTime today) =>
        stock.Where(s => s.Expires.Date >= today.Date && s.Expires.Date <= today.Date.AddDays(LeadDays))
            .OrderBy(s => s.Expires).ToList();

    /// <summary>Tonight's recipe: what the plan says, or nothing when the day is empty.</summary>
    public static Recipe? Tonight(IReadOnlyList<PlanSlot> plan, IReadOnlyList<Recipe> recipes, DateTime today)
    {
        var slot = plan.FirstOrDefault(s => s.Date.Date == today.Date);
        return slot?.RecipeId is { } id ? recipes.FirstOrDefault(r => r.Id == id) : null;
    }

    /// <summary>One recipe for tonight, uniformly at random. Null when the box is empty.</summary>
    public static Recipe? Suggest(IReadOnlyList<Recipe> recipes, Random random) =>
        recipes.Count == 0 ? null : recipes[random.Next(recipes.Count)];

    /// <summary>The line under the header: what ran out, what is close, and what tonight is.</summary>
    public static string SummaryOf(IReadOnlyList<StockItem> stock, IReadOnlyList<PlanSlot> plan,
        IReadOnlyList<Recipe> recipes, DateTime today, IMeowsText text)
    {
        var expired = Expired(stock, today).Count;
        var soon = Soon(stock, today).Count;

        if (expired > 0 && soon > 0)
            return text.Format("pantry.summary.both", expired, soon);
        if (expired > 0)
            return text.Format("pantry.summary.expired", expired);
        if (soon > 0)
            return text.Format("pantry.summary.soon", soon);

        var tonight = Tonight(plan, recipes, today);
        if (tonight is not null)
            return text.Format("pantry.summary.tonight", tonight.Title.Trim().Length > 0 ? tonight.Title.Trim() : text["pantry.untitled"]);

        return stock.Count == 0 && recipes.Count == 0 ? "" : text["pantry.summary.clear"];
    }

    /// <summary>The Home line: red while something ran out or is about to.</summary>
    public static Glance? GlanceOf(IReadOnlyList<StockItem> stock, IReadOnlyList<PlanSlot> plan,
        IReadOnlyList<Recipe> recipes, DateTime today, IMeowsText text)
    {
        var summary = SummaryOf(stock, plan, recipes, today, text);
        return summary.Length == 0
            ? null
            : new Glance(summary, Expired(stock, today).Count > 0 || Soon(stock, today).Count > 0);
    }

    /// <summary>How one item reads: days ago, today, tomorrow, or in so many days.</summary>
    public static string ExpiryText(StockItem item, DateTime today, IMeowsText text, System.Globalization.CultureInfo culture)
    {
        var date = item.Expires.ToString("d", culture);
        var days = (int)(item.Expires.Date - today.Date).TotalDays;

        return days switch
        {
            0 => text.Format("pantry.expiry.today", date),
            1 => text.Format("pantry.expiry.tomorrow", date),
            < 0 => text.Format("pantry.expiry.late", date, -days),
            _ => text.Format("pantry.expiry.left", date, days),
        };
    }

    /// <summary>Minutes as a person would say them. Zero is untimed, not instant.</summary>
    public static string TimeText(int minutes, IMeowsText text) =>
        minutes <= 0 ? text["pantry.time.unknown"] : text.Format("pantry.time.minutes", minutes);
}
