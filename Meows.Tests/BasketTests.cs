using Meows.Plugins.Abstractions;
using Meows.Plugins.Basket;
using Meows.Plugins.Basket.Services;
using Meows.Plugins.Basket.ViewModels;

namespace Meows.Tests;

/// <summary>
/// The arithmetic behind the board. Every one of these is told what today is rather than reading
/// the clock, so a run that starts before midnight cannot answer differently to the one after it.
/// </summary>
public class BoardTests
{
    private static readonly DateTime Today = new(2026, 10, 5);

    private static IMeowsText Text() => TestStrings.Load();

    private static List<BasketList> BoardWith(params (string List, string Title, DateTime? Due, bool Done)[] cards)
    {
        var lists = new List<BasketList> { new() { Title = "To do" } };
        foreach (var (list, title, due, done) in cards)
        {
            var target = lists.FirstOrDefault(l => l.Title == list) ?? lists[0];
            target.Cards.Add(new BasketCard { Title = title, Due = due, Done = done });
        }
        return lists;
    }

    [Fact]
    public void An_empty_board_has_nothing_to_say()
    {
        var text = Text();

        Assert.Equal("", Board.SummaryOf([], Today, text));
        Assert.Null(Board.GlanceOf([], Today, text));
    }

    [Fact]
    public void Late_first_then_coming_up_then_nothing_due()
    {
        var text = Text();

        Assert.Equal("1 overdue, 1 due soon",
            Board.SummaryOf(BoardWith(("To do", "Late", Today.AddDays(-2), false), ("To do", "Soon", Today.AddDays(3), false)), Today, text));
        Assert.Equal("2 overdue",
            Board.SummaryOf(BoardWith(("To do", "A", Today.AddDays(-1), false), ("To do", "B", Today.AddDays(-9), false)), Today, text));
        Assert.Equal("1 due soon",
            Board.SummaryOf(BoardWith(("To do", "Soon", Today, false)), Today, text));
        Assert.Equal("3 cards, nothing due",
            Board.SummaryOf(BoardWith(("To do", "A", null, false), ("To do", "B", Today.AddDays(200), false), ("To do", "C", null, false)), Today, text));
    }

    [Fact]
    public void Finished_cards_do_not_count_as_due()
    {
        var text = Text();
        var lists = BoardWith(("To do", "Was late", Today.AddDays(-5), true));

        Assert.Equal("1 cards, nothing due", Board.SummaryOf(lists, Today, text));
        Assert.False(Board.GlanceOf(lists, Today, text)?.IsTrouble);
    }

    [Fact]
    public void The_glance_is_red_only_while_something_is_late()
    {
        var text = Text();

        Assert.True(Board.GlanceOf(BoardWith(("To do", "Late", Today.AddDays(-1), false)), Today, text)?.IsTrouble);
        Assert.False(Board.GlanceOf(BoardWith(("To do", "Soon", Today.AddDays(1), false)), Today, text)?.IsTrouble);
    }

    [Fact]
    public void A_card_reads_today_tomorrow_days_or_finished()
    {
        var text = Text();
        var culture = System.Globalization.CultureInfo.GetCultureInfo("en-GB");

        Assert.Equal("No due date", Board.DueText(new BasketCard { Title = "x" }, Today, text, culture));
        Assert.Contains("today", Board.DueText(new BasketCard { Due = Today }, Today, text, culture));
        Assert.Contains("tomorrow", Board.DueText(new BasketCard { Due = Today.AddDays(1) }, Today, text, culture));
        Assert.Contains("in 4 days", Board.DueText(new BasketCard { Due = Today.AddDays(4) }, Today, text, culture));
        Assert.Contains("3 days ago", Board.DueText(new BasketCard { Due = Today.AddDays(-3) }, Today, text, culture));
        Assert.Equal("finished", Board.DueText(new BasketCard { Due = Today.AddDays(-3), Done = true }, Today, text, culture));
    }

    [Fact]
    public void The_export_names_lists_cards_and_boxes()
    {
        var lists = BoardWith(("To do", "Buy milk", Today, false), ("Done", "Paid rent", null, true));
        lists[0].Cards[0].Checklist.Add(new BasketCheckItem { Title = "Oat milk", Done = true });

        var markdown = Board.ExportMarkdown(lists, Today);

        Assert.Contains("## To do", markdown);
        Assert.Contains("- [ ] Buy milk", markdown);
        Assert.Contains("- [x] Paid rent", markdown);
        Assert.Contains("- [x] Oat milk", markdown);
    }

    [Fact]
    public void A_file_name_becomes_a_title_a_person_would_write()
    {
        Assert.Equal("Quarterly Report", Board.TitleFromPath(@"C:\docs\quarterly_report.pdf"));
        Assert.Equal("Waschmaschine Bosch", Board.TitleFromPath("20240314-waschmaschine-bosch.jpg"));
    }

    [Fact]
    public void A_fresh_board_starts_with_three_lists()
    {
        var lists = new List<BasketList>();

        Board.EnsureDefaults(lists);

        Assert.Equal(["To do", "Doing", "Done"], lists.Select(l => l.Title));
    }
}

/// <summary>
/// The tab itself: lists, cards, moving, finishing, and the half that matters when the tab is
/// not open. A card that falls due has to be said out loud on the shell's own surface, and
/// taken back down again when it is dealt with.
/// </summary>
public sealed class BasketTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "basket-" + Guid.NewGuid().ToString("N")[..10]);

    public BasketTests()
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

    /// <summary>
    /// A due date as the picker would hand it over: the day only, with no offset, so the card
    /// lands on the same day whatever timezone the machine running the test is in.
    /// </summary>
    private static DateTimeOffset DueIn(int days) =>
        new(DateTime.SpecifyKind(DateTime.Today.AddDays(days), DateTimeKind.Unspecified), TimeSpan.Zero);

    [Fact]
    public void Opens_with_something_to_say()
    {
        using var model = new BasketViewModel(Host("open"));

        Assert.False(string.IsNullOrWhiteSpace(model.Status));
        Assert.False(model.HasError);
        Assert.Equal(3, model.Lists.Count);
    }

    [Fact]
    public void Refresh_reports_when_it_ran()
    {
        using var model = new BasketViewModel(Host("refresh"));

        model.RefreshCommand.Execute(null);

        Assert.Contains(":", model.Status);
    }

    [Fact]
    public void Cards_are_added_moved_and_finished_with_journal_lines()
    {
        var host = Host("flow");
        using var model = new BasketViewModel(host);

        model.SelectedList = model.Lists[0];
        model.AddCardCommand.Execute(null);
        var card = Assert.Single(model.Lists[0].Cards);
        Assert.Equal("New card", card.Title);

        model.SelectedCard = card;
        Assert.True(model.MoveRightCommand.CanExecute(null));
        model.MoveRightCommand.Execute(null);

        Assert.Empty(model.Lists[0].Cards);
        Assert.Equal(card.Id, Assert.Single(model.Lists[1].Cards).Id);
        Assert.Equal(model.Lists[1], model.SelectedList);

        model.FinishCommand.Execute(null);
        Assert.True(model.Lists[1].Cards[0].Done);

        Assert.Contains(host.Store.Events, e => e.Kind == "added");
        Assert.Contains(host.Store.Events, e => e.Kind == "moved");
        Assert.Contains(host.Store.Events, e => e.Kind == "finished");
    }

    [Fact]
    public void A_card_that_falls_due_is_raised_and_taken_down_when_finished()
    {
        var host = Host("due");
        using var model = new BasketViewModel(host);

        model.SelectedList = model.Lists[0];
        model.AddCardCommand.Execute(null);
        model.SelectedCard!.Title = "TÜV";
        model.SelectedCard.DueOn = DueIn(-2);

        Assert.Single(host.Conditions);

        model.FinishCommand.Execute(null);

        Assert.Empty(host.Conditions);
    }

    [Fact]
    public void Something_far_off_is_not_news_yet()
    {
        var host = Host("far");
        using var model = new BasketViewModel(host);

        model.SelectedList = model.Lists[0];
        model.AddCardCommand.Execute(null);
        model.SelectedCard!.DueOn = DueIn(200);

        Assert.Empty(host.Conditions);
        Assert.False(((IGlanceable)model).Glance()?.IsTrouble);
    }

    [Fact]
    public void The_board_survives_the_tab_being_closed_and_opened_again()
    {
        var host = Host("persist");

        using (var model = new BasketViewModel(host))
        {
            model.SelectedList = model.Lists[0];
            model.AddCardCommand.Execute(null);
            var card = model.Lists[0].Cards[0];
            card.Title = "Insurance";
            card.Notes = "Renews in spring";
            model.NewCheckTitle = "Call them";
            model.SelectedCard = card;
            model.AddCheckCommand.Execute(null);
        }

        using var again = new BasketViewModel(host);

        var reopened = Assert.Single(again.Lists[0].Cards);
        Assert.Equal("Insurance", reopened.Title);
        Assert.Equal("Renews in spring", reopened.Notes);
        Assert.Equal("Call them", Assert.Single(reopened.Checks).Title);
    }

    [Fact]
    public void Search_finds_a_card_and_lands_on_it()
    {
        var host = Host("search");
        using var model = new BasketViewModel(host);

        model.SelectedList = model.Lists[0];
        model.AddCardCommand.Execute(null);
        model.Lists[0].Cards[0].Title = "Book the cabin for winter";

        var hit = Assert.Single(model.Search("cabin", 5));
        Assert.Equal("Book the cabin for winter", hit.Title);
        Assert.Contains("To do", hit.Detail);

        hit.Open();

        Assert.Equal("Book the cabin for winter", model.SelectedCard?.Shown);
    }

    [Fact]
    public void Files_handed_over_become_linked_cards_and_the_sender_hears_how_many()
    {
        var host = Host("handoff");
        using var model = new BasketViewModel(host);
        var first = Path.Combine(_root, "quote.pdf");
        var second = Path.Combine(_root, "photo.jpg");
        File.WriteAllText(first, "x");
        File.WriteAllText(second, "x");
        string? heard = null;

        Assert.True(model.Accepts(Handoff.Files([first, second])));
        Assert.False(model.Accepts(Handoff.Folder(Path.Combine(_root, "missing"))));
        model.Receive(Handoff.Files([first, second]) with { Reply = o => heard = o });

        Assert.Equal(2, model.Lists.SelectMany(l => l.Cards).Count());
        Assert.Equal("2 added to Basket", heard);
        Assert.Contains(model.Lists.SelectMany(l => l.Cards), c => c.LinkPath == first);
    }

    [Fact]
    public async Task A_rule_adds_a_card_once_with_the_file_and_the_other_plugins_words()
    {
        var host = Host("rule");
        using var model = new BasketViewModel(host);
        var file = Path.Combine(_root, "invoice.pdf");
        File.WriteAllText(file, "paper");

        ActionRequest Asked(string action, string subject, string? detail = null) =>
            new(action, new StoredEvent(7, DateTime.Now, "meows.birdwatch", "saved", subject, detail,
                new Dictionary<string, string>()));

        var said = await model.Perform(Asked(BasketPlugin.AddAction, file, "From @someone"), CancellationToken.None);

        var card = Assert.Single(model.Lists[0].Cards).Card;
        Assert.Equal("Invoice", card.Title);
        Assert.Equal(file, card.LinkPath);
        Assert.Equal("From @someone", card.Notes);
        Assert.Contains("Invoice", said);
        Assert.Null(model.SelectedCard);

        var again = await model.Perform(Asked(BasketPlugin.AddAction, file), CancellationToken.None);
        Assert.Single(model.Lists[0].Cards);
        Assert.Contains("already", again);

        await Assert.ThrowsAsync<ActionDeclinedException>(() => model.Perform(Asked("juggle", file), CancellationToken.None));
    }

    [Fact]
    public async Task Export_writes_markdown_with_the_board_in_it()
    {
        var host = Host("export");
        using var model = new BasketViewModel(host);
        var target = Path.Combine(_root, "board.md");
        host.Picks.Answers.Enqueue(target);

        model.SelectedList = model.Lists[0];
        model.AddCardCommand.Execute(null);
        model.Lists[0].Cards[0].Title = "Buy milk";

        await model.ExportAsync();

        var written = File.ReadAllText(target);
        Assert.Contains("## To do", written);
        Assert.Contains("Buy milk", written);
        Assert.Contains("1 cards exported", model.Status);
    }

    [Fact]
    public void While_off_answers_from_settings_and_a_hit_hands_it_to_itself()
    {
        var inner = Host("off");
        string cardId;
        using (var model = new BasketViewModel(inner))
        {
            model.SelectedList = model.Lists[0];
            model.AddCardCommand.Execute(null);
            var card = model.SelectedCard!;
            cardId = card.Id;
            // Through the view model, which is what saves. Setting the stored card directly would
            // change the field and never reach the settings file.
            card.Title = "Fridge warranty";
            card.Notes = "policy";
        }

        var dormant = new Dormant(inner, "meows.basket");
        ReachesItself(dormant);
        var asleep = new BasketPlugin().WhileOff(dormant);
        Assert.NotNull(asleep);

        var hit = Assert.Single(asleep.Search("warranty", 6));
        Assert.Equal("Fridge warranty", hit.Title);

        hit.Open();

        var (to, what) = Assert.Single(dormant.Handoffs.Sent);
        Assert.Equal("meows.basket", to);
        Assert.Equal(BasketPlugin.ShowVerb, what.Verb);
        Assert.Equal(cardId, what.Note);
    }

    [Fact]
    public void While_off_with_nothing_kept_has_nothing_to_search()
    {
        var dormant = new Dormant(Host("empty-off"), "meows.basket");

        Assert.Null(new BasketPlugin().WhileOff(dormant));
    }

    [Fact]
    public void Glance_while_off_says_what_the_tab_would_say()
    {
        var inner = Host("glance-off");
        using (var model = new BasketViewModel(inner))
        {
            model.SelectedList = model.Lists[0];
            model.AddCardCommand.Execute(null);
            model.SelectedCard!.Title = "TÜV";
            model.SelectedCard.DueOn = DueIn(-1);
        }

        var glance = new BasketPlugin().GlanceWhileOff(new Dormant(inner, "meows.basket"));

        Assert.NotNull(glance);
        Assert.True(glance!.IsTrouble);

        using var open = new BasketViewModel(inner);
        Assert.Equal(open.Glance(), glance);
    }

    [Fact]
    public async Task The_due_job_reports_and_notifies_when_something_is_late()
    {
        var inner = Host("job");
        using (var model = new BasketViewModel(inner))
        {
            model.SelectedList = model.Lists[0];
            model.AddCardCommand.Execute(null);
            model.SelectedCard!.Title = "TÜV";
            model.SelectedCard.DueOn = DueIn(-1);
        }

        var jobs = new BasketPlugin().Jobs;
        Assert.Contains(jobs, j => j.Id == BasketPlugin.DueJob);

        var jobHost = new JobHost(inner);
        var said = await new BasketPlugin().RunJob(BasketPlugin.DueJob, jobHost, CancellationToken.None);

        Assert.Contains("overdue", said);
        var (title, _, trouble) = Assert.Single(jobHost.Notices);
        Assert.Equal("Basket", title);
        Assert.True(trouble);

        await Assert.ThrowsAsync<JobDeclinedException>(() => new BasketPlugin().RunJob("sweep", jobHost, CancellationToken.None));
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

    private static void ReachesItself(Dormant host) => host.Handoffs.Reachable.Add(host.PluginId);

    /// <summary>What a --do job is given: the dormant host plus saving, logging and notifying.</summary>
    private sealed class JobHost(FakeHost inner) : IMeowsJobHost
    {
        public string PluginId => "meows.basket";

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
