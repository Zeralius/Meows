using Meows.Plugins.Abstractions;
using Meows.Services;

namespace Meows.Tests;

/// <summary>
/// The rules, run against a real store with the plugins played by functions: a line lands, the
/// right rule asks the right plugin, and History gets the account. The guards are the point:
/// one hop, one at a time per plugin, a runaway paused, a rule whose plugin is gone kept.
/// </summary>
public sealed class InstinctTests : IDisposable
{
    private const string Birdwatch = "meows.birdwatch";
    private const string Portion = "meows.portion";
    private const string Collar = "meows.collar";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "meows-instinct-" + Guid.NewGuid().ToString("N"));
    private readonly MeowsStore _store;
    private readonly List<InstinctRule> _rules = [];
    private readonly List<(string Target, ActionRequest Request)> _asked = [];
    private readonly List<string> _log = [];
    private int _saves;

    private List<InstinctPlugin> _plugins =
    [
        new(Birdwatch, "Birdwatch", [], [new RecordedKind("saved", "birdwatch.records.saved")]),
        new(Portion, "Portion", [new PluginAction("check", "portion.action.check")], [new RecordedKind("shrunk", "portion.records.shrunk")]),
        new(Collar, "Collar", [new PluginAction("remind", "collar.action.today")], []),
    ];

    /// <summary>What a plugin does when asked. Defaults to saying it did it.</summary>
    private Func<string, ActionRequest, CancellationToken, Task<string>> _perform = (_, request, _) => Task.FromResult("did " + request.Action);

    public InstinctTests()
    {
        _store = new MeowsStore(_root, _ => { });
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private InstinctEngine Engine(TimeSpan? patience = null, Func<DateTime>? now = null)
    {
        var engine = new InstinctEngine(_rules, () => _plugins,
            (target, request, token) =>
            {
                lock (_asked)
                    _asked.Add((target, request));
                return _perform(target, request, token);
            },
            _store.For(InstinctEngine.PluginId), () => _saves++, (line, _) => { lock (_log) _log.Add(line); },
            work => work(), now, patience);
        _store.Stored += engine.Consider;
        return engine;
    }

    private static InstinctRule Rule(string source = Birdwatch, string kind = "saved", string target = Portion, string action = "check", string? matching = null) =>
        new() { Source = source, Kind = kind, Target = target, Action = action, Matching = matching };

    private IReadOnlyList<StoredEvent> Account() => _store.Events(InstinctEngine.PluginId, null, null, 100);

    [Fact]
    public async Task A_line_that_matches_asks_the_target_and_history_says_what_came_back()
    {
        using var engine = Engine();
        var rule = Rule();
        engine.Add(rule);

        _store.For(Birdwatch).Record("saved", @"E:\intake\a.jpg", "From @someone");
        await engine.Idle();

        var (target, request) = Assert.Single(_asked);
        Assert.Equal(Portion, target);
        Assert.Equal("check", request.Action);
        Assert.Equal(@"E:\intake\a.jpg", request.Subject);
        Assert.Equal(Birdwatch, request.Cause.Plugin);

        var line = Assert.Single(Account());
        Assert.Equal("fired", line.Kind);
        Assert.Equal(@"E:\intake\a.jpg", line.Subject);
        Assert.Contains("did check", line.Detail);
        Assert.Contains("Birdwatch", line.Detail);
        Assert.Equal(rule.Id, line.Data["rule"]);

        Assert.Equal(1, rule.Fired);
        Assert.Equal("did check", rule.LastOutcome);
        Assert.NotNull(rule.LastFired);
    }

    [Fact]
    public async Task Another_plugin_another_kind_or_a_word_that_is_not_there_asks_nobody()
    {
        using var engine = Engine();
        engine.Add(Rule(matching: "paws"));

        _store.For(Birdwatch).Record("seen", @"E:\intake\paws.jpg");
        _store.For(Collar).Record("saved", @"E:\intake\paws.jpg");
        _store.For(Birdwatch).Record("saved", @"E:\intake\tail.jpg", "From @someone");
        await engine.Idle();
        Assert.Empty(_asked);

        _store.For(Birdwatch).Record("saved", @"E:\intake\PAWS.jpg");
        _store.For(Birdwatch).Record("saved", @"E:\intake\x.jpg", "From @pawsome");
        await engine.Idle();
        Assert.Equal(2, _asked.Count);
    }

    [Fact]
    public async Task What_a_plugin_records_while_doing_what_a_rule_asked_never_starts_another_rule()
    {
        using var engine = Engine();
        engine.Add(Rule());
        engine.Add(Rule(source: Portion, kind: "shrunk", target: Collar, action: "remind"));

        // Portion, asked to check, shrinks something: once straight away and once after work
        // that hopped to the thread pool, which is where a real plugin's scan finishes.
        _perform = async (target, request, _) =>
        {
            _store.For(target).Record("shrunk", @"E:\queue\big.jpg");
            await Task.Run(() => _store.For(target).Record("shrunk", @"E:\queue\bigger.jpg"));
            return "shrank two";
        };

        _store.For(Birdwatch).Record("saved", @"E:\intake\a.jpg");
        await engine.Idle();

        Assert.Equal([Portion], _asked.Select(a => a.Target));
        var shrunk = _store.Events(Portion, "shrunk", null, 10);
        Assert.Equal(2, shrunk.Count);
        Assert.All(shrunk, e => Assert.True(e.Data.ContainsKey(InstinctScope.DataKey)));
        Assert.Contains(_log, l => l.Contains("rules do not start rules"));
    }

    [Fact]
    public async Task A_line_from_a_plugin_that_is_busy_for_a_rule_is_treated_as_the_rules_doing()
    {
        using var engine = Engine();
        engine.Add(Rule());
        engine.Add(Rule(source: Portion, kind: "shrunk", target: Collar, action: "remind"));

        var holding = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _perform = (_, _, _) => holding.Task;

        _store.For(Birdwatch).Record("saved", @"E:\intake\a.jpg");

        // Written from somewhere the mark does not reach, while Portion is still at it.
        await Task.Run(() => _store.For(Portion).Record("shrunk", @"E:\queue\big.jpg"));
        holding.SetResult("done");
        await engine.Idle();

        Assert.Equal([Portion], _asked.Select(a => a.Target));

        // Once Portion is free, the same kind of line is its own again.
        _store.For(Portion).Record("shrunk", @"E:\queue\other.jpg");
        await engine.Idle();
        Assert.Equal([Portion, Collar], _asked.Select(a => a.Target));
    }

    [Fact]
    public async Task One_thing_at_a_time_per_plugin_in_the_order_asked()
    {
        using var engine = Engine();
        engine.Add(Rule());

        var running = 0;
        var most = 0;
        _perform = async (_, request, _) =>
        {
            most = Math.Max(most, Interlocked.Increment(ref running));
            await Task.Delay(20);
            Interlocked.Decrement(ref running);
            return request.Subject;
        };

        for (var i = 0; i < 4; i++)
            _store.For(Birdwatch).Record("saved", $@"E:\intake\{i}.jpg");
        await engine.Idle();

        Assert.Equal(1, most);
        Assert.Equal([@"E:\intake\0.jpg", @"E:\intake\1.jpg", @"E:\intake\2.jpg", @"E:\intake\3.jpg"],
            _asked.Select(a => a.Request.Subject));
    }

    [Fact]
    public async Task A_rule_that_fires_far_too_often_is_paused_says_so_and_can_be_resumed()
    {
        using var engine = Engine();
        var rule = Rule();
        engine.Add(rule);

        for (var i = 0; i < InstinctEngine.RunawayLimit + 5; i++)
            _store.For(Birdwatch).Record("saved", $@"E:\intake\{i}.jpg");
        await engine.Idle();

        Assert.Equal(InstinctEngine.RunawayLimit, _asked.Count);
        Assert.NotNull(rule.PausedReason);
        Assert.Equal(RuleState.Paused, engine.StateOf(rule, out var why));
        Assert.Equal(rule.PausedReason, why);
        Assert.Single(Account(), e => e.Kind == "paused");

        engine.Resume(rule);
        Assert.Null(rule.PausedReason);
        _store.For(Birdwatch).Record("saved", @"E:\intake\after.jpg");
        await engine.Idle();
        Assert.Equal(InstinctEngine.RunawayLimit + 1, _asked.Count);
    }

    [Fact]
    public async Task Declined_and_failed_are_written_down_as_different_things()
    {
        using var engine = Engine();
        engine.Add(Rule());

        _perform = (_, request, _) => request.Subject.EndsWith("a.jpg")
            ? throw new ActionDeclinedException("no bot folder yet")
            : throw new InvalidOperationException("it broke");

        _store.For(Birdwatch).Record("saved", @"E:\intake\a.jpg");
        _store.For(Birdwatch).Record("saved", @"E:\intake\b.jpg");
        await engine.Idle();

        var lines = Account().OrderBy(e => e.Id).ToList();
        Assert.Equal(["declined", "failed"], lines.Select(e => e.Kind));
        Assert.Contains("no bot folder yet", lines[0].Detail);
        Assert.Contains("it broke", lines[1].Detail);
    }

    [Fact]
    public async Task An_action_that_never_ends_is_stopped_waiting_for_and_called_off()
    {
        using var engine = Engine(patience: TimeSpan.FromMilliseconds(50));
        engine.Add(Rule());

        var cancelled = false;
        _perform = async (_, _, token) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                throw;
            }
            return "never";
        };

        _store.For(Birdwatch).Record("saved", @"E:\intake\a.jpg");
        await engine.Idle();

        Assert.Equal("failed", Assert.Single(Account()).Kind);
        Assert.True(cancelled);
    }

    [Fact]
    public async Task A_rule_whose_plugin_has_gone_is_kept_says_why_and_fires_again_when_it_is_back()
    {
        using var engine = Engine();
        var rule = Rule();
        engine.Add(rule);
        var all = _plugins;
        _plugins = all.Where(p => p.Id != Portion).ToList();

        Assert.Equal(RuleState.TargetMissing, engine.StateOf(rule, out var why));
        Assert.Contains(Portion, why);
        Assert.Contains("meows.portion", engine.Describe(rule));

        _store.For(Birdwatch).Record("saved", @"E:\intake\a.jpg");
        await engine.Idle();
        Assert.Empty(_asked);
        Assert.Contains(rule, engine.Rules);

        _plugins = all;
        _store.For(Birdwatch).Record("saved", @"E:\intake\b.jpg");
        await engine.Idle();
        Assert.Single(_asked);
    }

    [Fact]
    public async Task A_rule_switched_off_does_nothing_and_nothing_fires_on_instincts_own_lines()
    {
        using var engine = Engine();
        var off = Rule();
        engine.Add(off);
        engine.SetEnabled(off, false);
        engine.Add(Rule(source: InstinctEngine.PluginId, kind: "fired", target: Collar, action: "remind"));

        _store.For(Birdwatch).Record("saved", @"E:\intake\a.jpg");
        _store.For(InstinctEngine.PluginId).Record("fired", "anything");
        await engine.Idle();

        Assert.Empty(_asked);
        Assert.Equal(RuleState.Off, engine.StateOf(off, out _));
    }

    [Fact]
    public void The_sentence_uses_what_the_plugins_say_and_the_saved_words_when_they_say_nothing()
    {
        using var engine = Engine();
        var declared = Rule(matching: "paws");
        var undeclared = Rule(kind: "posted");

        Assert.Equal("When Birdwatch saved a picture matching \u201cpaws\u201d, Portion: Check the queues", engine.Describe(declared));
        Assert.Equal("When Birdwatch recorded \u201cposted\u201d, Portion: Check the queues", engine.Describe(undeclared));
    }

    [Fact]
    public void Adding_removing_and_switching_off_save_and_say_so()
    {
        using var engine = Engine();
        var changes = 0;
        engine.Changed += () => changes++;

        var rule = Rule();
        engine.Add(rule);
        engine.SetEnabled(rule, false);
        engine.SetEnabled(rule, false);
        engine.Remove(rule);

        Assert.Equal(3, _saves);
        Assert.Equal(3, changes);
        Assert.Empty(engine.Rules);
    }

    [Fact]
    public void A_moved_file_is_found_where_it_went()
    {
        var moved = new StoredEvent(1, DateTime.Now, "meows.kibble", "sent", @"E:\in\a.jpg", null,
            new Dictionary<string, string> { [ActionRequest.DestinationKey] = @"E:\bot\Paws\To_Send\a.jpg" });
        var stayed = new StoredEvent(2, DateTime.Now, Birdwatch, "saved", @"E:\in\b.jpg", null, new Dictionary<string, string>());

        Assert.Equal(@"E:\bot\Paws\To_Send\a.jpg", new ActionRequest("clean", moved).Path);
        Assert.Equal(@"E:\in\a.jpg", new ActionRequest("clean", moved).Subject);
        Assert.Equal(@"E:\in\b.jpg", new ActionRequest("clean", stayed).Path);
    }

    [Fact]
    public void The_store_hands_over_the_whole_line_and_knows_which_kinds_a_plugin_writes()
    {
        StoredEvent? heard = null;
        _store.Stored += e => heard = e;

        _store.For(Birdwatch).Record("saved", @"E:\a.jpg", "From @x", new Dictionary<string, string> { ["post"] = "1" });
        _store.For(Birdwatch).Record("saved", @"E:\b.jpg");
        _store.For(Birdwatch).Record("seen", @"E:\c.jpg");

        Assert.NotNull(heard);
        Assert.Equal("seen", heard!.Kind);
        Assert.Equal(_store.Events(Birdwatch, null, null, 1)[0].Id, heard.Id);
        Assert.Equal(["saved", "seen"], _store.Kinds(Birdwatch));
        Assert.Empty(_store.Kinds(Collar));
    }

    [Fact]
    public void The_rules_tab_offers_what_plugins_declare_and_what_they_have_written_and_makes_the_rule()
    {
        using var engine = Engine();
        _store.For(Birdwatch).Record("posted", @"E:.jpg");
        _store.For(Birdwatch).Record("saved", @"E:.jpg");
        using var tab = new Meows.ViewModels.RulesViewModel(engine, () => _plugins, id => _store.Kinds(id));

        Assert.Equal(["Birdwatch", "Collar", "Portion"], tab.Sources.Select(s => s.Name));
        Assert.Equal(["Collar", "Portion"], tab.Targets.Select(t => t.Name));
        Assert.False(tab.CanAdd);

        tab.SelectedSource = tab.Sources.Single(s => s.Id == Birdwatch);
        Assert.Equal(["saved a picture", "recorded \u201cposted\u201d"], tab.Kinds.Select(k => k.Label));
        tab.SelectedKind = tab.Kinds.Single(k => k.Kind == "posted");
        tab.SelectedTarget = tab.Targets.Single(t => t.Id == Portion);
        Assert.Equal("Check the queues", tab.SelectedAction?.Label);
        tab.Matching = "  paws ";

        // A rescan or a language change reads everything again and keeps what was picked.
        tab.Refresh();
        Assert.Equal("posted", tab.SelectedKind?.Kind);
        Assert.Equal(Portion, tab.SelectedTarget?.Id);

        Assert.True(tab.AddCommand.CanExecute(null));
        tab.AddCommand.Execute(null);

        var rule = Assert.Single(engine.Rules);
        Assert.Equal((Birdwatch, "posted", "paws", Portion, "check"), (rule.Source, rule.Kind, rule.Matching, rule.Target, rule.Action));
        var row = Assert.Single(tab.Rows);
        Assert.False(row.HasProblem);
        Assert.Equal("", tab.Matching);

        row.Enabled = false;
        Assert.False(rule.Enabled);
        tab.RemoveCommand.Execute(row);
        Assert.Empty(tab.Rows);
    }

    [Fact]
    public void Rules_are_kept_with_the_preferences_and_come_back()
    {
        var settings = new ShellSettings(Path.Combine(_root, "settings"), previousRoot: Path.Combine(_root, "none"));
        var preferences = settings.LoadPreferences();
        preferences.Rules.Add(new InstinctRule { Source = Birdwatch, Kind = "saved", Matching = "paws", Target = Portion, Action = "check", Enabled = false });
        settings.SavePreferences(preferences);

        var back = Assert.Single(settings.LoadPreferences().Rules);
        Assert.Equal(preferences.Rules[0].Id, back.Id);
        Assert.Equal("paws", back.Matching);
        Assert.False(back.Enabled);
    }
}
