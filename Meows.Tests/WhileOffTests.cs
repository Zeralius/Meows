using Meows.Plugins.Abstractions;
using Meows.Plugins.Collar;
using Meows.Plugins.Collar.Services;
using Meows.Plugins.Collar.ViewModels;
using Meows.Plugins.Familiar;
using Meows.Plugins.Kibble;

namespace Meows.Tests;

/// <summary>
/// Contract 1.0.0: a plugin that is switched off can still be searched from Ctrl+K, from what it
/// kept, and a hit hands the plugin to itself so the shell switches it on and lands on the
/// thing. Collar is the first to answer; a plugin that does not is simply left out.
/// </summary>
public sealed class WhileOffTests
{
    /// <summary>The little a plugin gets while off: its settings, its journal, a handoff.</summary>
    private sealed class Dormant : IMeowsDormantHost
    {
        private readonly object? _settings;

        public Dormant(object? settings) => _settings = settings;

        public string PluginId { get; init; } = "meows.collar";

        public string DataDirectory => Path.GetTempPath();

        public IMeowsText Text => MeowsText.Current;

        public IMeowsStore Store => NoStore.Instance;

        public FakeHost.FakeHandoff Handoffs { get; } = new();

        public IMeowsHandoff Handoff => Handoffs;

        public T? LoadSettings<T>() where T : class => _settings as T;
    }

    [Fact]
    public void A_plugin_that_does_not_say_is_left_out()
    {
        // A default interface member: reached through the interface, as the shell reaches it.
        IMeowsPlugin bare = new BarePluginTests.Bare();
        Assert.Null(bare.WhileOff(new Dormant(null)));
    }

    [Fact]
    public void Collar_with_nothing_kept_has_nothing_to_search()
    {
        Assert.Null(new CollarPlugin().WhileOff(new Dormant(null)));
        Assert.Null(new CollarPlugin().WhileOff(new Dormant(new CollarSettings())));
    }

    [Fact]
    public void Collar_answers_from_its_settings_and_a_hit_hands_it_to_itself()
    {
        var settings = new CollarSettings
        {
            Entries =
            [
                new CollarEntry { Id = "a1", Title = "Fridge warranty", Kind = Kind.Warranty, Due = new DateTime(2027, 3, 1) },
                new CollarEntry { Id = "b2", Title = "Car insurance", Kind = Kind.Insurance, Due = new DateTime(2026, 12, 1), File = @"C:\docs\policy.pdf" },
            ],
        };
        var host = new Dormant(settings);
        host.Handoffs.Reachable.Add("meows.collar");

        var asleep = new CollarPlugin().WhileOff(host);
        Assert.NotNull(asleep);

        var hits = asleep.Search("policy", 6);
        var hit = Assert.Single(hits);
        Assert.Equal("Car insurance", hit.Title);
        Assert.Contains("Insurance", hit.Detail);

        hit.Open();

        var (to, what) = Assert.Single(host.Handoffs.Sent);
        Assert.Equal("meows.collar", to);
        Assert.Equal(CollarPlugin.ShowVerb, what.Verb);
        Assert.Equal("b2", what.Note);
        Assert.Empty(what.Paths);
    }

    [Fact]
    public void Kibble_answers_with_what_is_waiting_in_its_last_folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "kibble-off-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "cat-on-roof.jpg"), "x");
            File.WriteAllText(Path.Combine(folder, "dog.png"), "x");
            var host = new Dormant(new Meows.Plugins.Kibble.ViewModels.KibbleSettings { LastSourceFolder = folder }) { PluginId = "meows.kibble" };
            host.Handoffs.Reachable.Add("meows.kibble");

            var asleep = new KibblePlugin().WhileOff(host);
            Assert.NotNull(asleep);
            var hit = Assert.Single(asleep.Search("roof", 6));
            Assert.Equal("cat-on-roof.jpg", hit.Title);

            hit.Open();
            var (_, what) = Assert.Single(host.Handoffs.Sent);
            Assert.Equal(KibblePlugin.ShowVerb, what.Verb);
            Assert.Equal(Path.Combine(folder, "cat-on-roof.jpg"), what.Note);

            Assert.Null(new KibblePlugin().WhileOff(new Dormant(new Meows.Plugins.Kibble.ViewModels.KibbleSettings { LastSourceFolder = Path.Combine(folder, "missing") })));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Familiar_answers_with_its_kits_by_name()
    {
        var root = Path.Combine(Path.GetTempPath(), "familiar-off-" + Guid.NewGuid().ToString("N")[..8]);
        foreach (var kit in new[] { "Bench", "Exports", "Frames", "Curse of Strahd", "Lost Mine" })
            Directory.CreateDirectory(Path.Combine(root, kit));
        try
        {
            var host = new Dormant(new Meows.Plugins.Familiar.ViewModels.FamiliarSettings { Root = root }) { PluginId = "meows.familiar" };
            host.Handoffs.Reachable.Add("meows.familiar");

            var asleep = new FamiliarPlugin().WhileOff(host);
            Assert.NotNull(asleep);
            Assert.Equal(["Curse of Strahd", "Lost Mine"], asleep.Search("s", 6).Select(h => h.Title));

            var hit = Assert.Single(asleep.Search("mine", 6));
            hit.Open();
            var (_, what) = Assert.Single(host.Handoffs.Sent);
            Assert.Equal(FamiliarPlugin.KitVerb, what.Verb);
            Assert.Equal(Path.Combine(root, "Lost Mine"), what.Note);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Collar_takes_the_show_handoff_and_selects_the_entry()
    {
        var host = new FakeHost(Path.Combine(Path.GetTempPath(), "collar-" + Guid.NewGuid().ToString("N")[..8]));
        host.SaveSettings(new CollarSettings
        {
            Entries =
            [
                new CollarEntry { Id = "a1", Title = "One", Due = DateTime.Today.AddDays(40) },
                new CollarEntry { Id = "b2", Title = "Two", Due = DateTime.Today.AddDays(50) },
            ],
        });
        var model = new CollarViewModel(host);

        var show = new Handoff(CollarPlugin.ShowVerb, [], "b2");
        Assert.True(model.Accepts(show));
        model.Receive(show);

        Assert.Equal("Two", model.Selected?.Shown);
        Assert.False(model.Accepts(new Handoff(CollarPlugin.ShowVerb, [], null)));
        model.Dispose();
    }
}
