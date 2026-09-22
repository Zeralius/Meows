using Meows.Plugins.Abstractions;
using Nudge;
using Yarn;

namespace Meows.Tests;

/// <summary>
/// The examples under examples/ are copied by people, so the behaviour each one is there to
/// show is pinned here: what the README says it does, it does. The view smoke test covers
/// their views; this covers the contract calls behind them.
/// </summary>
public sealed class ExampleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "meows-examples-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class Dormant(object? settings) : IMeowsDormantHost
    {
        public string PluginId => "example.yarn";

        public string DataDirectory => Path.GetTempPath();

        public IMeowsText Text => MeowsText.Current;

        public IMeowsStore Store => NoStore.Instance;

        public FakeHost.FakeHandoff Handoffs { get; } = new() { Reachable = { "example.yarn" } };

        public IMeowsHandoff Handoff => Handoffs;

        public T? LoadSettings<T>() where T : class => settings as T;
    }

    [Fact]
    public void Yarn_keeps_a_thread_in_settings_and_writes_the_keep_to_the_journal()
    {
        var host = new FakeHost(_root);
        using var model = new YarnViewModel(host) { Draft = "  buy catnip  " };

        model.KeepCommand.Execute(null);

        Assert.Equal(["buy catnip"], model.Threads);
        Assert.Equal(["buy catnip"], host.LoadSettings<YarnSettings>()!.Threads);
        var kept = Assert.Single(host.Store.Events);
        Assert.Equal("kept", kept.Kind);
        Assert.Equal("buy catnip", kept.Subject);
        Assert.Single(model.Journal);
        Assert.Equal("", model.Draft);
    }

    [Fact]
    public void Yarn_puts_a_keep_back_only_while_the_thread_is_still_there()
    {
        var host = new FakeHost(_root);
        using var model = new YarnViewModel(host) { Draft = "vet on Friday" };
        model.KeepCommand.Execute(null);
        var kept = host.Store.Events.Single();

        Assert.True(model.CanUndo(kept));
        Assert.Null(model.Undo(kept));
        Assert.Empty(model.Threads);

        // Gone now: the History tab shows no button, and a second try says why.
        Assert.False(model.CanUndo(kept));
        Assert.NotNull(model.Undo(kept));
    }

    [Fact]
    public void Yarn_is_searched_on_and_off_and_a_hit_while_off_hands_it_to_itself()
    {
        var host = new FakeHost(_root);
        using var model = new YarnViewModel(host) { Draft = "renew the domain" };
        model.KeepCommand.Execute(null);

        var on = Assert.Single(model.Search("domain", 5));
        on.Open();
        Assert.Equal("renew the domain", model.Selected);

        var dormant = new Dormant(host.LoadSettings<YarnSettings>());
        var asleep = new YarnPlugin().WhileOff(dormant);
        Assert.NotNull(asleep);
        var off = Assert.Single(asleep.Search("renew", 5));
        off.Open();
        var (to, handoff) = Assert.Single(dormant.Handoffs.Sent);
        Assert.Equal("example.yarn", to);
        Assert.Equal(YarnViewModel.ShowVerb, handoff.Verb);

        // Delivered, the handoff lands on the thread, which is what the palette promised.
        model.Selected = null;
        Assert.True(model.Accepts(handoff));
        model.Receive(handoff);
        Assert.Equal("renew the domain", model.Selected);

        Assert.Null(new YarnPlugin().WhileOff(new Dormant(null)));
    }

    [Fact]
    public void Nudge_answers_what_arrives_and_is_told_when_nobody_is_there()
    {
        var host = new FakeHost(_root);
        using var model = new NudgeViewModel(host);

        string? answered = null;
        var files = Handoff.Files([@"C:\a.png", @"C:\b.png"]) with { Reply = o => answered = o };
        Assert.True(model.Accepts(files));
        Assert.False(model.Accepts(new Handoff("something.else", [])));
        model.Receive(files);

        Assert.Single(model.Arrived);
        Assert.Contains("2", answered);

        // Nothing reachable: the button stays off, and a send is refused rather than dropped.
        Assert.False(model.NudgeChonkCommand.CanExecute(null));
        Assert.Empty(host.Handoffs.Sent);
    }
}
