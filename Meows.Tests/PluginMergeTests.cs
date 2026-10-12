using Meows.Plugins;
using Meows.Plugins.Abstractions;
using Meows.Services;

namespace Meows.Tests;

/// <summary>
/// Plugins taken in as pages of another (6.1.0): Catnip and Mouser into Litter, Weigh-In into
/// Chonk, Perch into Telegram Poster. What was theirs carries over, an old copy of one is refused,
/// and a job keeps the name a scheduled task knows it by.
/// </summary>
public sealed class PluginMergeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "merges-" + Guid.NewGuid().ToString("N")[..10]);

    public PluginMergeTests()
    {
        Directory.CreateDirectory(_root);
        TestStrings.Install();
    }

    private static IReadOnlyList<PluginDescriptor> Shipped() =>
        [.. ShippedPlugins.Types.Select(t => PluginDescriptor.Loaded((IMeowsPlugin)Activator.CreateInstance(t)!, @"C:\plugins\x\x.dll"))];

    [Fact]
    public void The_shipped_plugins_say_what_they_took_in()
    {
        var map = PluginMerges.Map(Shipped());

        Assert.Equal("meows.litter", map["meows.catnip"]);
        Assert.Equal("meows.litter", map["meows.mouser"]);
        Assert.Equal("meows.chonk", map["meows.weighin"]);
        Assert.Equal("meows.telegram-poster", map["meows.perch"]);
        Assert.Equal(4, map.Count);
    }

    [Fact]
    public void No_two_pages_of_one_plugin_offer_the_same_action_or_job()
    {
        foreach (var plugin in ShippedPlugins.Types.Select(t => (IMeowsPlugin)Activator.CreateInstance(t)!))
        {
            Assert.Equal(plugin.Actions.Count, plugin.Actions.Select(a => a.Id).Distinct().Count());
            Assert.Equal(plugin.Jobs.Count, plugin.Jobs.Select(j => j.Id).Distinct().Count());
        }
    }

    [Fact]
    public void A_job_of_a_page_keeps_the_name_a_scheduled_task_already_uses()
    {
        var chonk = Shipped().Single(d => d.Id == "meows.chonk");
        var job = chonk.Plugin!.Jobs.Single(j => j.Id == "measure");

        Assert.Equal("weighin.measure", JobRunner.NameOf(chonk, job));
    }

    [Fact]
    public void An_old_copy_of_an_absorbed_plugin_is_refused_and_says_where_it_went()
    {
        var found = Shipped().Append(PluginDescriptor.Loaded(new OldCatnip(), @"C:\plugins\Meows.Plugins.Catnip\Meows.Plugins.Catnip.dll")).ToList();

        var checkedOver = PluginMerges.RefuseAbsorbed(found, PluginMerges.Map(found));

        var old = checkedOver.Single(d => d.AssemblyPath.Contains("Catnip"));
        Assert.False(old.IsCompatible);
        Assert.Contains("Litter", old.IncompatibleReason);
        Assert.True(checkedOver.Single(d => d.Id == "meows.litter").IsCompatible);
    }

    private sealed class OldCatnip : IMeowsPlugin
    {
        public string Id => "meows.catnip";
        public string DisplayName => "Catnip";
        public string Description => "";
        public string? Icon => null;
        public Avalonia.Controls.Control CreateView(IMeowsHost host) => throw new NotSupportedException();
    }

    [Fact]
    public void What_was_the_absorbed_plugins_carries_over_once()
    {
        var settings = new ShellSettings(_root, Path.Combine(_root, "no-mews"));
        var old = Path.Combine(_root, "plugins", "meows.weighin");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "settings.json"), "{\"budgets\":[]}");
        settings.SaveActivatedPlugins(["meows.weighin", "meows.kibble"]);

        var preferences = settings.LoadPreferences();
        preferences.Personas.Add(new PersonaSetting { Name = "Mine", PluginIds = ["meows.weighin", "meows.chonk", "meows.purr"] });
        preferences.Rules.Add(new InstinctRule { Source = "meows.kibble", Target = "meows.weighin", Action = "measure" });
        preferences.HomeOrder = ["meows.weighin", "meows.kibble"];
        var store = new MeowsStore(_root, _ => { });
        store.For("meows.weighin").Record("measured", "C:\\");
        store.For("meows.weighin").Set("last", "yesterday");

        var map = new Dictionary<string, string> { ["meows.weighin"] = "meows.chonk" };
        var said = PluginMerges.Apply(map, settings, preferences, store);

        Assert.NotEmpty(said);
        Assert.Equal("{\"budgets\":[]}", File.ReadAllText(Path.Combine(_root, "plugins", "meows.chonk", "meows.weighin", "settings.json")));
        Assert.True(Directory.Exists(old + PluginMerges.MovedSuffix));
        Assert.False(Directory.Exists(old));
        Assert.Equal(["meows.chonk", "meows.kibble"], settings.LoadActivatedPlugins().Order());
        var saved = settings.LoadPreferences();
        Assert.Equal(["meows.chonk", "meows.purr"], saved.Personas.Single(p => p.Name == "Mine").PluginIds.Order());
        Assert.Equal("meows.chonk", saved.Rules.Single().Target);
        Assert.Equal(["meows.chonk", "meows.kibble"], saved.HomeOrder);
        Assert.Single(store.For("meows.chonk").Recent(kind: "measured"));
        Assert.Empty(store.For("meows.weighin").Recent());

        // The page reads its facts under its own prefix, which is where they went.
        Assert.Equal("yesterday", store.For("meows.chonk").Get("meows.weighin:last"));

        // Nothing left to do the second time.
        Assert.Empty(PluginMerges.Apply(map, settings, saved, store));
    }

    [Fact]
    public void A_page_gets_a_folder_and_settings_of_its_own_under_the_main_plugin()
    {
        var host = new FakeHost(Path.Combine(_root, "host"));
        var nested = new NestedHost(host, "meows.perch");

        nested.SaveSettings(new Dictionary<string, string> { ["a"] = "b" });
        nested.Store.Set("seen", "1");

        Assert.Equal(Path.Combine(host.DataDirectory, "meows.perch"), nested.DataDirectory);
        Assert.True(File.Exists(Path.Combine(nested.DataDirectory, "settings.json")));
        Assert.Equal("b", nested.LoadSettings<Dictionary<string, string>>()!["a"]);
        Assert.Equal("1", host.Store.Get("meows.perch:seen"));
        Assert.Equal(host.PluginId, nested.PluginId);
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
}
