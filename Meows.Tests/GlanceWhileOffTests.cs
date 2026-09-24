using System.Text.Json;
using Avalonia.Controls;
using Meows.Plugins;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Collar;
using Meows.Plugins.Collar.Services;
using Meows.Plugins.Collar.ViewModels;
using Meows.Plugins.WeighIn;
using Meows.Plugins.WeighIn.Services;
using Meows.Services;

namespace Meows.Tests;

/// <summary>
/// Contract 1.4.0 and <c>--glance</c>: the Home tab's lines with no window. A plugin answers from
/// what it keeps; one that says nothing gets the shell's line, the last thing it recorded; a
/// plugin that is off is not on the list at all.
/// </summary>
public sealed class GlanceWhileOffTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "meows-glanceoff-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ShellSettings _settings;
    private readonly List<string> _log = [];

    public GlanceWhileOffTests()
    {
        _settings = new ShellSettings(Path.Combine(_root, "settings"), Path.Combine(_root, "no-mews"));
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

    private sealed class Plain(string id, Func<IMeowsDormantHost, Glance?> glance) : IMeowsPlugin
    {
        public string Id => id;

        public string DisplayName => id.Split('.')[^1];

        public string Description => "";

        public string? Icon => null;

        public Control CreateView(IMeowsHost host) => throw new NotSupportedException();

        public Glance? GlanceWhileOff(IMeowsDormantHost host) => glance(host);
    }

    private static PluginDescriptor Loaded(IMeowsPlugin plugin) => PluginDescriptor.Loaded(plugin, "/plugins/" + plugin.Id + ".dll");

    private DormantHost Dormant(string id) => new(id, _settings, NoHandoff.Instance, null);

    [Fact]
    public void Collar_says_from_its_settings_what_its_tab_would_say()
    {
        var entries = new List<CollarEntry>
        {
            new() { Title = "TÜV", Kind = Kind.Inspection, Due = DateTime.Today.AddDays(-3), RepeatMonths = 24 },
            new() { Title = "Laptop", Kind = Kind.Warranty, Due = DateTime.Today.AddDays(12) },
        };
        _settings.SavePluginSettings("meows.collar", new CollarSettings { Entries = entries });

        var glance = new CollarPlugin().GlanceWhileOff(Dormant("meows.collar"));

        Assert.NotNull(glance);
        Assert.True(glance!.IsTrouble);
        Assert.Equal("1 have passed, 1 more coming up", glance.Text);

        var host = new FakeHost(Path.Combine(_root, "collar-host"));
        host.SaveSettings(new CollarSettings { Entries = entries });
        using var open = new CollarViewModel(host);
        Assert.Equal(open.Glance(), glance);
    }

    [Fact]
    public void Collar_with_no_dates_and_weigh_in_with_no_readings_say_what_their_tabs_say()
    {
        Assert.Null(new CollarPlugin().GlanceWhileOff(Dormant("meows.collar")));

        var weighIn = new WeighInPlugin().GlanceWhileOff(Dormant("meows.weighin"));
        Assert.Equal("No readings yet", weighIn?.Text);

        var folder = Path.Combine(_settings.PluginDataDirectory("meows.weighin"), "readings");
        Readings.Save(folder, new Reading(new DateTime(2026, 9, 1, 8, 0, 0), []));
        Readings.Save(folder, new Reading(new DateTime(2026, 9, 23, 9, 30, 0), []));
        var read = new WeighInPlugin().GlanceWhileOff(Dormant("meows.weighin"));
        Assert.Contains("23 Sep 09:30", read?.Text);
        Assert.False(read?.IsTrouble);
    }

    [Fact]
    public void Only_switched_on_plugins_are_listed_and_the_quiet_ones_get_their_last_line()
    {
        var store = new MeowsStore(Path.Combine(_root, "store"), _ => { });
        store.For("test.quiet").Record("sent", @"E:\in\cat.jpg", "queued for Paws");
        IEnumerable<PluginDescriptor> found =
        [
            Loaded(new Plain("test.loud", _ => new Glance("2 would fail to post", IsTrouble: true))),
            Loaded(new Plain("test.quiet", _ => null)),
            Loaded(new Plain("test.empty", _ => null)),
            Loaded(new Plain("test.broken", _ => throw new InvalidOperationException("no"))),
            Loaded(new Plain("test.off", _ => new Glance("should not be asked"))),
        ];
        var on = new HashSet<string>(["test.loud", "test.quiet", "test.empty", "test.broken"], StringComparer.OrdinalIgnoreCase);

        var lines = GlanceReport.Gather(found, on, _settings, store, _log.Add);

        Assert.Equal(["broken", "empty", "loud", "quiet"], lines.Select(l => l.Name));
        var loud = lines.Single(l => l.Id == "test.loud");
        Assert.True(loud.IsTrouble);
        Assert.True(loud.FromPlugin);
        var quiet = lines.Single(l => l.Id == "test.quiet");
        Assert.Contains("cat.jpg", quiet.Text);
        Assert.False(quiet.FromPlugin);
        Assert.Equal("nothing recorded yet", lines.Single(l => l.Id == "test.empty").Text);
        Assert.Equal("nothing recorded yet", lines.Single(l => l.Id == "test.broken").Text);
        Assert.Contains(_log, l => l.Contains("broken") && l.Contains("no"));
    }

    [Fact]
    public void As_text_the_trouble_is_marked_and_as_json_it_is_keyed_by_id()
    {
        IReadOnlyList<GlanceLine> lines =
        [
            new("meows.collar", "Collar", "1 have passed", IsTrouble: true, FromPlugin: true),
            new("meows.weighin", "Weigh-In", "Last reading 23 Sep", IsTrouble: false, FromPlugin: true),
        ];

        var text = GlanceReport.AsText(lines).Split(Environment.NewLine);
        Assert.Equal("! Collar    1 have passed", text[0]);
        Assert.Equal("  Weigh-In  Last reading 23 Sep", text[1]);
        Assert.Equal("No plugins are switched on.", GlanceReport.AsText([]));

        using var json = JsonDocument.Parse(GlanceReport.AsJson(lines, new DateTimeOffset(2026, 9, 24, 9, 0, 0, TimeSpan.Zero)));
        Assert.True(json.RootElement.GetProperty("trouble").GetBoolean());
        var first = json.RootElement.GetProperty("plugins")[0];
        Assert.Equal("meows.collar", first.GetProperty("id").GetString());
        Assert.True(first.GetProperty("isTrouble").GetBoolean());
        Assert.StartsWith("2026-09-24T09:00:00", json.RootElement.GetProperty("at").GetString());
    }
}
