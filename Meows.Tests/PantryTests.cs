using Meows.Plugins.Abstractions;
using Meows.Plugins.Pantry;
using Meows.Plugins.Pantry.Services;
using Meows.Plugins.Pantry.ViewModels;

namespace Meows.Tests;

/// <summary>
/// The arithmetic behind the pantry: what runs out, what the week says, and what tonight is.
/// Every one of these is told what today is rather than reading the clock.
/// </summary>
public class CookbookTests
{
    private static readonly DateTime Today = new(2026, 10, 5);

    private static IMeowsText Text() => TestStrings.Load();

    [Fact]
    public void The_plan_rolls_forward_and_always_starts_today()
    {
        var plan = new List<PlanSlot>
        {
            new() { Date = Today.AddDays(-2), RecipeId = "old" },
            new() { Date = Today.AddDays(3), RecipeId = "kept" },
        };

        Cookbook.RollPlan(plan, Today);

        Assert.Equal(7, plan.Count);
        Assert.Equal(Today, plan[0].Date);
        Assert.Equal("kept", plan.Single(s => s.Date == Today.AddDays(3)).RecipeId);
        Assert.DoesNotContain(plan, s => s.Date < Today);
    }

    [Fact]
    public void Late_close_and_tonight_read_in_that_order()
    {
        var text = Text();
        var recipes = new List<Recipe> { new() { Id = "r1", Title = "Soup" } };
        var plan = new List<PlanSlot> { new() { Date = Today, RecipeId = "r1" } };

        Assert.Equal("1 ran out, 1 close",
            Cookbook.SummaryOf([new StockItem { Expires = Today.AddDays(-1) }, new StockItem { Expires = Today.AddDays(1) }],
                [], [], Today, text));
        Assert.Equal("2 ran out",
            Cookbook.SummaryOf([new StockItem { Expires = Today.AddDays(-1) }, new StockItem { Expires = Today.AddDays(-9) }],
                [], [], Today, text));
        Assert.Equal("1 running out",
            Cookbook.SummaryOf([new StockItem { Expires = Today.AddDays(2) }], [], [], Today, text));
        Assert.Equal("Tonight: Soup", Cookbook.SummaryOf([], plan, recipes, Today, text));
        Assert.Equal("Nothing running out",
            Cookbook.SummaryOf([], [], [new Recipe { Title = "Soup" }], Today, text));
        Assert.Equal("", Cookbook.SummaryOf([], [], [], Today, text));
    }

    [Fact]
    public void The_glance_is_red_while_anything_runs_out()
    {
        var text = Text();

        Assert.Null(Cookbook.GlanceOf([], [], [], Today, text));
        Assert.True(Cookbook.GlanceOf([new StockItem { Expires = Today.AddDays(-1) }], [], [], Today, text)?.IsTrouble);
        Assert.True(Cookbook.GlanceOf([new StockItem { Expires = Today.AddDays(1) }], [], [], Today, text)?.IsTrouble);
        Assert.False(Cookbook.GlanceOf([], [new PlanSlot { Date = Today, RecipeId = "r1" }],
            [new Recipe { Id = "r1", Title = "Soup" }], Today, text)?.IsTrouble);
    }

    [Fact]
    public void Expiry_and_time_read_like_a_person()
    {
        var text = Text();
        var culture = System.Globalization.CultureInfo.GetCultureInfo("en-GB");

        Assert.Contains("today", Cookbook.ExpiryText(new StockItem { Expires = Today }, Today, text, culture));
        Assert.Contains("tomorrow", Cookbook.ExpiryText(new StockItem { Expires = Today.AddDays(1) }, Today, text, culture));
        Assert.Contains("in 2 days", Cookbook.ExpiryText(new StockItem { Expires = Today.AddDays(2) }, Today, text, culture));
        Assert.Contains("3 days ago", Cookbook.ExpiryText(new StockItem { Expires = Today.AddDays(-3) }, Today, text, culture));
        Assert.Equal("Untimed", Cookbook.TimeText(0, text));
        Assert.Equal("45 min", Cookbook.TimeText(45, text));
    }

    [Fact]
    public void Tonight_is_the_plan_and_suggest_draws_from_the_box()
    {
        var recipes = new List<Recipe> { new() { Id = "r1", Title = "Soup" }, new() { Id = "r2", Title = "Stew" } };

        Assert.Null(Cookbook.Tonight([], recipes, Today));
        Assert.Null(Cookbook.Suggest([], new Random(1)));

        var plan = new List<PlanSlot> { new() { Date = Today, RecipeId = "r2" } };
        Assert.Equal("Stew", Cookbook.Tonight(plan, recipes, Today)?.Title);
        Assert.Contains(recipes, r => r == Cookbook.Suggest(recipes, new Random(1)));
    }
}

/// <summary>
/// The tab itself: box, fridge and week, said out loud when the fridge runs out, and the half
/// that matters with no window.
/// </summary>
public sealed class PantryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pantry-" + Guid.NewGuid().ToString("N")[..10]);

    public PantryTests()
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
        }
    }

    private FakeHost Host(string name) => new(Path.Combine(_root, "host-" + name));

    [Fact]
    public void Opens_with_a_week_and_something_to_say()
    {
        using var model = new PantryViewModel(Host("open"));

        Assert.False(string.IsNullOrWhiteSpace(model.Status));
        Assert.False(model.HasError);
        Assert.Equal(7, model.Plan.Count);
    }

    [Fact]
    public void Refresh_reports_when_it_ran()
    {
        using var model = new PantryViewModel(Host("refresh"));

        model.RefreshCommand.Execute(null);

        Assert.Contains(":", model.Status);
    }

    [Fact]
    public void Recipes_are_added_typed_and_deleted_with_the_plan_following()
    {
        var host = Host("recipes");
        using var model = new PantryViewModel(host);

        model.AddRecipeCommand.Execute(null);
        var recipe = Assert.Single(model.Recipes);
        model.SelectedRecipe = recipe;
        recipe.Title = "Tomato soup";
        recipe.MinutesText = "45";
        recipe.TagsText = "soup, winter";

        Assert.Equal("45 min", recipe.TimeText);
        Assert.Contains(host.Store.Events, e => e.Kind == "added");

        model.SelectedDay = model.Plan[0];
        model.SelectedDay.PlannedChoice = model.SelectedDay.Options.Single(o => o.Id == recipe.Id);
        Assert.Equal("Tomato soup", model.Summary.Split("Tonight: ").Last());

        model.DeleteRecipeCommand.Execute(null);
        Assert.Empty(model.Recipes);
        Assert.All(model.Plan, p => Assert.Null(p.Slot.RecipeId));
    }

    [Fact]
    public void Suggesting_fills_tonight_and_cooking_journals_it()
    {
        var host = Host("tonight");
        using var model = new PantryViewModel(host);

        model.AddRecipeCommand.Execute(null);
        model.Recipes[0].Title = "Stew";

        model.SuggestCommand.Execute(null);

        Assert.Contains("Stew", model.Status);
        Assert.Contains(host.Store.Events, e => e.Kind == "suggested");

        model.SelectedDay = model.Plan.First(p => p.IsToday);
        model.CookCommand.Execute(null);
        Assert.Contains(host.Store.Events, e => e.Kind == "cooked");
        Assert.Contains("Stew", model.Status);
    }

    [Fact]
    public void Suggesting_with_an_empty_box_says_so()
    {
        using var model = new PantryViewModel(Host("emptybox"));

        model.SuggestCommand.Execute(null);

        Assert.Equal("The box is empty.", model.Status);
    }

    [Fact]
    public void What_runs_out_is_raised_and_taken_down_when_used()
    {
        var host = Host("stock");
        using var model = new PantryViewModel(host);

        model.NewStockName = "Spinach";
        model.AddStockCommand.Execute(null);
        model.SelectedStock = model.Stock[0];
        model.SelectedStock.ExpiresOn = new DateTimeOffset(
            DateTime.SpecifyKind(DateTime.Today.AddDays(-1), DateTimeKind.Unspecified), TimeSpan.Zero);

        Assert.Single(host.Conditions);

        model.UseUpCommand.Execute(null);

        Assert.Empty(host.Conditions);
        Assert.Empty(model.Stock);
    }

    [Fact]
    public void The_box_fridge_and_week_survive_reopening()
    {
        var host = Host("persist");

        using (var model = new PantryViewModel(host))
        {
            model.AddRecipeCommand.Execute(null);
            model.Recipes[0].Title = "Soup";
            model.NewStockName = "Milk";
            model.AddStockCommand.Execute(null);
            model.SelectedDay = model.Plan[2];
            model.SelectedDay.PlannedChoice = model.SelectedDay.Options.Single(o => o.Id == model.Recipes[0].Id);
        }

        using var again = new PantryViewModel(host);

        Assert.Equal("Soup", Assert.Single(again.Recipes).Title);
        Assert.Equal("Milk", Assert.Single(again.Stock).Name);
        Assert.Equal(7, again.Plan.Count);
    }

    [Fact]
    public void Markdown_files_handed_over_become_recipes()
    {
        var host = Host("handoff");
        using var model = new PantryViewModel(host);
        var recipe = Path.Combine(_root, "pancakes.md");
        File.WriteAllText(recipe, "# Pancakes\n\nFlour, eggs, milk.");
        var notes = Path.Combine(_root, "notes.pdf");
        File.WriteAllText(notes, "x");
        string? heard = null;

        Assert.True(model.Accepts(Handoff.Files([recipe])));
        Assert.False(model.Accepts(Handoff.Files([notes])));
        model.Receive(Handoff.Files([recipe]) with { Reply = o => heard = o });

        var added = Assert.Single(model.Recipes);
        Assert.Equal("pancakes", added.Title);
        Assert.Contains("Flour", added.Body);
        Assert.Equal("1 added to Pantry", heard);
    }

    [Fact]
    public void Search_finds_a_recipe_and_lands_on_it()
    {
        var host = Host("search");
        using var model = new PantryViewModel(host);

        model.AddRecipeCommand.Execute(null);
        model.Recipes[0].Title = "Pumpkin curry";
        model.Recipes[0].TagsText = "autumn";

        var hit = Assert.Single(model.Search("autumn", 5));
        Assert.Equal("Pumpkin curry", hit.Title);

        hit.Open();
        Assert.Equal("Pumpkin curry", model.SelectedRecipe?.Shown);
    }

    [Fact]
    public void While_off_answers_from_the_box_and_a_hit_hands_it_to_itself()
    {
        var inner = Host("off");
        string recipeId;
        using (var model = new PantryViewModel(inner))
        {
            model.AddRecipeCommand.Execute(null);
            var recipe = model.SelectedRecipe!;
            recipeId = recipe.Id;
            // Through the view model, which is what saves. Setting the stored recipe directly
            // would change the field and never reach the settings file.
            recipe.Title = "Lentil soup";
            recipe.TagsText = "winter";
        }

        var dormant = new Dormant(inner, "meows.pantry");
        dormant.Handoffs.Reachable.Add("meows.pantry");
        var asleep = new PantryPlugin().WhileOff(dormant);
        Assert.NotNull(asleep);

        var hit = Assert.Single(asleep.Search("lentil", 6));
        Assert.Equal("Lentil soup", hit.Title);

        hit.Open();
        var (to, what) = Assert.Single(dormant.Handoffs.Sent);
        Assert.Equal("meows.pantry", to);
        Assert.Equal(PantryPlugin.ShowVerb, what.Verb);
        Assert.Equal(recipeId, what.Note);
    }

    [Fact]
    public void While_off_with_nothing_kept_has_nothing_to_search()
    {
        var dormant = new Dormant(Host("empty-off"), "meows.pantry");

        Assert.Null(new PantryPlugin().WhileOff(dormant));
    }

    [Fact]
    public void Glance_while_off_says_what_the_tab_would_say()
    {
        var inner = Host("glance-off");
        using (var model = new PantryViewModel(inner))
        {
            model.AddRecipeCommand.Execute(null);
            model.Recipes[0].Title = "Soup";
            model.NewStockName = "Milk";
            model.AddStockCommand.Execute(null);
        }

        var glance = new PantryPlugin().GlanceWhileOff(new Dormant(inner, "meows.pantry"));

        Assert.NotNull(glance);
        Assert.True(glance!.IsTrouble);

        using var open = new PantryViewModel(inner);
        Assert.Equal(open.Glance(), glance);
    }

    [Fact]
    public async Task The_expiring_job_reports_the_fridge_and_declines_nothing()
    {
        var inner = Host("job");
        using (var model = new PantryViewModel(inner))
        {
            model.NewStockName = "Yogurt";
            model.AddStockCommand.Execute(null);
            model.SelectedStock = model.Stock[0];
            model.SelectedStock.ExpiresOn = new DateTimeOffset(
                DateTime.SpecifyKind(DateTime.Today.AddDays(-2), DateTimeKind.Unspecified), TimeSpan.Zero);
        }

        Assert.Contains(new PantryPlugin().Jobs, j => j.Id == PantryPlugin.ExpiringJob);

        var jobHost = new JobHost(inner);
        var said = await new PantryPlugin().RunJob(PantryPlugin.ExpiringJob, jobHost, CancellationToken.None);

        Assert.Contains("ran out", said);
        var (title, _, trouble) = Assert.Single(jobHost.Notices);
        Assert.Equal("Pantry", title);
        Assert.True(trouble);

        await Assert.ThrowsAsync<JobDeclinedException>(() => new PantryPlugin().RunJob("sweep", jobHost, CancellationToken.None));
    }

    [Fact]
    public async Task A_rule_fills_an_empty_tonight_and_declines_a_planned_one()
    {
        var inner = Host("rule");
        using var model = new PantryViewModel(inner);
        model.AddRecipeCommand.Execute(null);
        model.Recipes[0].Title = "Omelette";

        ActionRequest Asked(string action) =>
            new(action, new StoredEvent(7, DateTime.Now, "meows.weighin", "reading", @"F:\", null,
                new Dictionary<string, string>()));

        var said = await model.Perform(Asked(PantryPlugin.SuggestAction), CancellationToken.None);
        Assert.Contains("Omelette", said);
        // Asked by a rule, not a person looking: tonight is filled, the selection is untouched.
        Assert.Equal("Omelette", model.Plan.First(p => p.IsToday).PlannedChoice?.Shown);

        await Assert.ThrowsAsync<ActionDeclinedException>(() => model.Perform(Asked(PantryPlugin.SuggestAction), CancellationToken.None));
        await Assert.ThrowsAsync<ActionDeclinedException>(() => model.Perform(Asked("juggle"), CancellationToken.None));
    }

    /// <summary>The little a plugin gets while off: its settings and a way to hand itself the thing found.</summary>
    private sealed class Dormant(FakeHost inner, string pluginId) : IMeowsDormantHost
    {
        public string PluginId { get; } = pluginId;

        public string DataDirectory => inner.DataDirectory;

        public IMeowsText Text => MeowsText.Current;

        public IMeowsStore Store => inner.Store;

        public FakeHost.FakeHandoff Handoffs { get; } = new();

        public IMeowsHandoff Handoff => Handoffs;

        public T? LoadSettings<T>() where T : class => inner.LoadSettings<T>();
    }

    /// <summary>What a --do job is given: the dormant host plus saving, logging and notifying.</summary>
    private sealed class JobHost(FakeHost inner) : IMeowsJobHost
    {
        public string PluginId => "meows.pantry";

        public string DataDirectory => inner.DataDirectory;

        public IMeowsText Text => MeowsText.Current;

        public IMeowsStore Store => inner.Store;

        public IMeowsHandoff Handoff => inner.Handoffs;

        public T? LoadSettings<T>() where T : class => inner.LoadSettings<T>();

        public void SaveSettings<T>(T settings) where T : class => inner.SaveSettings(settings);

        public void Log(string message) => inner.Log(message);

        public void Report(string status)
        {
        }

        public List<(string Title, string Text, bool Trouble)> Notices { get; } = [];

        public void Notify(string title, string text, bool isTrouble = false) => Notices.Add((title, text, isTrouble));
    }
}
