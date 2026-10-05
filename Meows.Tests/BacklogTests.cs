using Meows.Disk;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Backlog;
using Meows.Plugins.Backlog.Services;
using Meows.Plugins.Backlog.ViewModels;

namespace Meows.Tests;

/// <summary>
/// The arithmetic behind the pile, plus a fake Steam library made of manifests only.
/// </summary>
public class PileTests
{
    private static IMeowsText Text() => TestStrings.Load();

    private static SteamInstall Install(string id, string name) =>
        new(id, name, 1_000_000, null, null, @"F:\games", Path.Combine(@"F:\games", "steamapps", "common", name));

    [Fact]
    public void Steam_arrivals_land_on_the_pile_and_nothing_leaves_on_its_own()
    {
        var entries = new List<BacklogEntry>();

        Assert.Equal(2, Pile.EnsureFromSteam(entries, [Install("10", "Alpha"), Install("20", "Beta")]));
        Assert.All(entries, e => Assert.Equal(BacklogStatus.Backlog, e.Status));

        // Second read: a rename lands, the uninstalled game stays a wish.
        Assert.Equal(0, Pile.EnsureFromSteam(entries, [Install("10", "Alpha Remastered")]));
        Assert.Equal(2, entries.Count);
        Assert.Equal("Alpha Remastered", entries.Single(e => e.Id == "10").Name);
        Assert.Contains(entries, e => e.Id == "20");
    }

    [Fact]
    public void Only_what_is_waiting_or_playing_can_be_picked()
    {
        var entries = new List<BacklogEntry>
        {
            new() { Id = "1", Name = "Shelved", Status = BacklogStatus.Shelved },
            new() { Id = "2", Name = "Done", Status = BacklogStatus.Done },
        };

        Assert.Null(Pile.Pick(entries, new Random(1)));
        Assert.Empty(Pile.Candidates(entries));

        entries.Add(new BacklogEntry { Id = "3", Name = "Waiting", Status = BacklogStatus.Backlog });
        Assert.Equal("Waiting", Pile.Pick(entries, new Random(1))?.Name);
    }

    [Fact]
    public void Weights_favour_the_unrated_the_liked_and_what_is_playing()
    {
        var unrated = new BacklogEntry { Id = "u", Status = BacklogStatus.Backlog };
        var liked = new BacklogEntry { Id = "l", Status = BacklogStatus.Backlog, Rating = 5 };
        var playing = new BacklogEntry { Id = "p", Status = BacklogStatus.Playing, Rating = 1 };

        Assert.Equal(3, Pile.WeightOf(unrated));
        Assert.Equal(6, Pile.WeightOf(liked));
        Assert.Equal(3, Pile.WeightOf(playing));
    }

    [Fact]
    public void The_header_counts_what_is_waiting_and_what_is_done()
    {
        var text = Text();

        Assert.Equal("", Pile.SummaryOf([], text));
        Assert.Null(Pile.GlanceOf([], text));

        var entries = new List<BacklogEntry>
        {
            new() { Id = "1", Status = BacklogStatus.Backlog },
            new() { Id = "2", Status = BacklogStatus.Playing },
            new() { Id = "3", Status = BacklogStatus.Done },
        };
        Assert.Equal("2 to play, 1 done", Pile.SummaryOf(entries, text));
        var glance = Pile.GlanceOf(entries, text);
        Assert.NotNull(glance);
        Assert.False(glance!.IsTrouble);
    }

    [Fact]
    public void Stars_say_unrated_rather_than_zero()
    {
        var text = Text();

        Assert.Equal("Unrated", Pile.Stars(0, text));
        Assert.Equal("★★★", Pile.Stars(3, text));
    }
}

/// <summary>
/// The tab itself: Steam folded in, statuses journaled, picks said out loud, and the half that
/// matters with no window.
/// </summary>
public sealed class BacklogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "backlog-" + Guid.NewGuid().ToString("N")[..10]);

    public BacklogTests()
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

    /// <summary>A library with manifests only; Backlog never needs the games themselves.</summary>
    private string Library(string name, params (string Id, string Name, long Size, long? LastPlayed)[] games)
    {
        var library = Path.Combine(_root, name);
        var steamapps = Path.Combine(library, "steamapps");
        Directory.CreateDirectory(steamapps);
        foreach (var (id, game, size, played) in games)
        {
            var lines = new List<string>
            {
                "\"AppState\"", "{",
                $"\t\"appid\"\t\t\"{id}\"",
                $"\t\"name\"\t\t\"{game}\"",
                $"\t\"installdir\"\t\t\"{game.Replace(' ', '_')}\"",
                $"\t\"SizeOnDisk\"\t\t\"{size}\"",
                "\t\"LastUpdated\"\t\t\"1700000000\"",
            };
            if (played is { } seconds)
                lines.Add($"\t\"LastPlayed\"\t\t\"{seconds}\"");
            lines.Add("}");
            File.WriteAllLines(Path.Combine(steamapps, $"appmanifest_{id}.acf"), lines);
        }
        return library;
    }

    [Fact]
    public void Opens_with_something_to_say()
    {
        var library = Library("F", ("10", "Big Unplayed", 90_000, 0));
        using var model = new BacklogViewModel(Host("open"), () => [library]);
        model.Show(SteamLibrary.InstalledIn(library));

        Assert.False(string.IsNullOrWhiteSpace(model.Status));
        Assert.False(model.HasError);
        Assert.Single(model.Games);
    }

    [Fact]
    public void Steam_games_arrive_as_backlog_and_a_second_read_adds_nothing()
    {
        var host = Host("steam");
        var library = Library("F", ("10", "Alpha", 90_000, 0), ("20", "Beta", 10_000, 0));
        using var model = new BacklogViewModel(host, () => [library]);
        model.Show(SteamLibrary.InstalledIn(library));

        Assert.Equal(2, model.Games.Count);
        Assert.All(model.Games, g => Assert.Equal(BacklogStatus.Backlog, g.Entry.Status));

        model.Show(SteamLibrary.InstalledIn(library));
        Assert.Equal(2, model.Games.Count);

        using var again = new BacklogViewModel(host, () => [library]);
        again.Show(SteamLibrary.InstalledIn(library));
        Assert.Equal(2, again.Games.Count);
    }

    [Fact]
    public void Statuses_are_journaled_and_the_pile_survives_reopening()
    {
        var host = Host("status");
        var library = Library("F", ("10", "Alpha", 90_000, 0));

        using (var model = new BacklogViewModel(host, () => [library]))
        {
            model.Show(SteamLibrary.InstalledIn(library));
            model.Selected = model.Games[0];
            model.PlayCommand.Execute(null);
            model.Selected!.RatingChoice = model.Ratings.Single(c => c.Value == 4);
            model.DoneCommand.Execute(null);
        }

        Assert.Contains(host.Store.Events, e => e.Kind == "started");
        Assert.Contains(host.Store.Events, e => e.Kind == "finished");

        using var again = new BacklogViewModel(host, () => [library]);
        again.Show(SteamLibrary.InstalledIn(library));
        var reopened = Assert.Single(again.Games);
        Assert.Equal(BacklogStatus.Done, reopened.Entry.Status);
        Assert.Equal(4, reopened.Entry.Rating);
    }

    [Fact]
    public void Picking_selects_a_game_and_says_it_out_loud()
    {
        var host = Host("pick");
        var library = Library("F", ("10", "Alpha", 90_000, 0));
        using var model = new BacklogViewModel(host, () => [library]);
        model.Show(SteamLibrary.InstalledIn(library));

        model.PickCommand.Execute(null);

        Assert.Equal("Alpha", model.Selected?.Shown);
        Assert.Contains("Alpha", model.Status);
        Assert.Contains(host.Store.Events, e => e.Kind == "picked");
    }

    [Fact]
    public void A_game_steam_never_heard_of_can_be_added_by_hand()
    {
        var host = Host("manual");
        var library = Library("F", ("10", "Alpha", 90_000, 0));
        using var model = new BacklogViewModel(host, () => [library]);
        model.Show(SteamLibrary.InstalledIn(library));

        model.NewGameName = "Chess";
        model.AddCommand.Execute(null);

        var added = model.Games.Single(g => g.Entry.IsManual);
        Assert.Equal("Chess", added.Shown);
        Assert.Equal("not installed", added.SizeText);

        model.NewGameName = "chess";
        model.AddCommand.Execute(null);
        Assert.Single(model.Games, g => g.Entry.IsManual);
        Assert.Contains("already", model.Status);
    }

    [Fact]
    public void Forgetting_removes_the_thought_never_the_game()
    {
        var host = Host("forget");
        var library = Library("F", ("10", "Alpha", 90_000, 0));
        using var model = new BacklogViewModel(host, () => [library]);
        model.Show(SteamLibrary.InstalledIn(library));
        model.Selected = model.Games[0];

        model.ForgetCommand.Execute(null);

        Assert.Empty(model.Games);
        Assert.True(File.Exists(Path.Combine(library, "steamapps", "appmanifest_10.acf")));
    }

    [Fact]
    public void Search_finds_a_game_and_lands_on_it()
    {
        var host = Host("search");
        var library = Library("F", ("10", "Stardew Valley", 90_000, 0));
        using var model = new BacklogViewModel(host, () => [library]);
        model.Show(SteamLibrary.InstalledIn(library));

        var hit = Assert.Single(model.Search("stardew", 5));
        Assert.Equal("Stardew Valley", hit.Title);

        hit.Open();
        Assert.Equal("Stardew Valley", model.Selected?.Shown);
    }

    [Fact]
    public void While_off_answers_from_settings_and_a_hit_hands_it_to_itself()
    {
        var inner = Host("off");
        var library = Library("F", ("10", "Hades", 90_000, 0));
        string entryId;
        using (var model = new BacklogViewModel(inner, () => [library]))
        {
            model.Show(SteamLibrary.InstalledIn(library));
            entryId = model.Games[0].Id;
        }

        var dormant = new Dormant(inner, "meows.backlog");
        dormant.Handoffs.Reachable.Add("meows.backlog");
        var asleep = new BacklogPlugin().WhileOff(dormant);
        Assert.NotNull(asleep);

        var hit = Assert.Single(asleep.Search("hades", 6));
        Assert.Equal("Hades", hit.Title);

        hit.Open();
        var (to, what) = Assert.Single(dormant.Handoffs.Sent);
        Assert.Equal("meows.backlog", to);
        Assert.Equal(BacklogPlugin.ShowVerb, what.Verb);
        Assert.Equal(entryId, what.Note);
    }

    [Fact]
    public void While_off_with_nothing_kept_has_nothing_to_search()
    {
        var dormant = new Dormant(Host("empty-off"), "meows.backlog");

        Assert.Null(new BacklogPlugin().WhileOff(dormant));
    }

    [Fact]
    public void Glance_while_off_says_what_the_tab_would_say()
    {
        var inner = Host("glance-off");
        var library = Library("F", ("10", "Hades", 90_000, 0));
        using (var model = new BacklogViewModel(inner, () => [library]))
            model.Show(SteamLibrary.InstalledIn(library));

        var glance = new BacklogPlugin().GlanceWhileOff(new Dormant(inner, "meows.backlog"));

        Assert.NotNull(glance);
        Assert.False(glance!.IsTrouble);

        using var open = new BacklogViewModel(inner, () => [library]);
        open.Show(SteamLibrary.InstalledIn(library));
        Assert.Equal(open.Glance(), glance);
    }

    [Fact]
    public async Task The_pick_job_names_tonights_game_and_declines_an_empty_pile()
    {
        var inner = Host("job");
        var library = Library("F", ("10", "Hades", 90_000, 0));
        using (var model = new BacklogViewModel(inner, () => [library]))
            model.Show(SteamLibrary.InstalledIn(library));

        Assert.Contains(new BacklogPlugin().Jobs, j => j.Id == BacklogPlugin.PickJob);

        var jobHost = new JobHost(inner);
        var said = await new BacklogPlugin().RunJob(BacklogPlugin.PickJob, jobHost, CancellationToken.None);

        Assert.Contains("Hades", said);
        var (title, _, _) = Assert.Single(jobHost.Notices);
        Assert.Equal("Backlog", title);

        await Assert.ThrowsAsync<JobDeclinedException>(() => new BacklogPlugin().RunJob("sweep", jobHost, CancellationToken.None));

        var empty = new JobHost(Host("job-empty"));
        await Assert.ThrowsAsync<JobDeclinedException>(() => new BacklogPlugin().RunJob(BacklogPlugin.PickJob, empty, CancellationToken.None));
    }

    [Fact]
    public async Task A_rule_gets_one_pick_as_a_sentence()
    {
        var inner = Host("rule");
        var library = Library("F", ("10", "Hades", 90_000, 0));
        using var model = new BacklogViewModel(inner, () => [library]);
        model.Show(SteamLibrary.InstalledIn(library));

        ActionRequest Asked(string action) =>
            new(action, new StoredEvent(7, DateTime.Now, "meows.weighin", "reading", @"F:\", null,
                new Dictionary<string, string>()));

        var said = await model.Perform(Asked(BacklogPlugin.PickAction), CancellationToken.None);

        Assert.Contains("Hades", said);
        Assert.Null(model.Selected);

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
        public string PluginId => "meows.backlog";

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
