using Avalonia.Controls;
using Meows.Plugins;
using Meows.Plugins.Abstractions;
using Meows.Plugins.WeighIn;
using Meows.Plugins.WeighIn.Services;
using Meows.Plugins.WeighIn.ViewModels;
using Meows.Services;

namespace Meows.Tests;

/// <summary>
/// Contract 1.5.0 and <c>--do</c>: a plugin's job with no window. Only for a plugin that is on,
/// stamped so the plugin's own schedule stands down, with an exit code a scheduled task can read;
/// and the toast it may raise escapes whatever it is given.
/// </summary>
public sealed class WorkWithoutWindowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "meows-do-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ShellSettings _settings;
    private readonly List<string> _said = [];

    public WorkWithoutWindowTests()
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

    private sealed class Worker(string id, Func<string, IMeowsJobHost, Task<string>> run) : IMeowsPlugin
    {
        public string Id => id;

        public string DisplayName => id.Split('.')[^1];

        public string Description => "";

        public string? Icon => null;

        public Control CreateView(IMeowsHost host) => throw new NotSupportedException();

        public IReadOnlyList<PluginJob> Jobs => [new("tidy", "Tidy up") { StandsInForSchedule = true }];

        public Task<string> RunJob(string jobId, IMeowsJobHost host, CancellationToken token) => run(jobId, host);
    }

    private static PluginDescriptor Loaded(IMeowsPlugin plugin) => PluginDescriptor.Loaded(plugin, "/plugins/" + plugin.Id + ".dll");

    private JobHost Host(PluginDescriptor plugin) =>
        new(plugin.Id, _settings, NoStore.Instance, new ShellLog(Path.Combine(_root, "do.log")), _said.Add, (title, text, _) => _said.Add($"{title}: {text}"));

    [Fact]
    public void A_name_is_the_plugin_and_the_job_with_or_without_meows_in_front()
    {
        Assert.Equal(("meows.weighin", "measure"), JobRunner.Parse("weighin.measure"));
        Assert.Equal(("meows.weighin", "measure"), JobRunner.Parse("meows.weighin.measure"));
        Assert.Equal(("someone.plugin", "go"), JobRunner.Parse("someone.plugin.go"));
        Assert.Null(JobRunner.Parse("weighin"));
        Assert.Null(JobRunner.Parse("weighin."));
    }

    [Fact]
    public async Task A_job_runs_only_when_its_plugin_is_on_and_is_stamped_for_the_schedule_to_see()
    {
        var worker = Loaded(new Worker("meows.worker", async (_, host) =>
        {
            host.Report("half way");
            host.Notify("Worker", "all tidy");
            await Task.Yield();
            return "tidied 3";
        }));
        IReadOnlySet<string> off = new HashSet<string>();
        IReadOnlySet<string> on = new HashSet<string> { "meows.worker" };

        var refused = await JobRunner.RunAsync([worker], off, "worker.tidy", Host, _settings.Root, CancellationToken.None);
        Assert.Equal(JobRunner.SwitchedOff, refused.ExitCode);
        Assert.False(OutsideRuns.IsFresh(_settings.Root, "meows.worker", "tidy", DateTime.UtcNow));

        Assert.Equal(JobRunner.Unknown, (await JobRunner.RunAsync([worker], on, "worker.sweep", Host, _settings.Root, CancellationToken.None)).ExitCode);

        var done = await JobRunner.RunAsync([worker], on, "worker.tidy", Host, _settings.Root, CancellationToken.None);
        Assert.Equal(JobRunner.Done, done.ExitCode);
        Assert.Equal("tidied 3", done.Said);
        Assert.Equal(["half way", "Worker: all tidy"], _said);
        Assert.True(OutsideRuns.IsFresh(_settings.Root, "meows.worker", "tidy", DateTime.UtcNow));
        Assert.False(OutsideRuns.IsFresh(_settings.Root, "meows.worker", "tidy", DateTime.UtcNow.AddDays(9)));

        Assert.Equal(["worker.tidy"], JobRunner.All([worker]).Select(j => JobRunner.NameOf(j.Plugin, j.Job)));
    }

    [Fact]
    public async Task A_declined_or_failing_job_says_so_with_a_failing_code()
    {
        IReadOnlySet<string> on = new HashSet<string> { "meows.worker" };
        var declines = Loaded(new Worker("meows.worker", (_, _) => throw new JobDeclinedException("nothing to tidy")));
        var breaks = Loaded(new Worker("meows.worker", (_, _) => throw new IOException("disk gone")));

        var declined = await JobRunner.RunAsync([declines], on, "worker.tidy", Host, _settings.Root, CancellationToken.None);
        var broke = await JobRunner.RunAsync([breaks], on, "worker.tidy", Host, _settings.Root, CancellationToken.None);

        Assert.Equal((JobRunner.Failed, "nothing to tidy"), (declined.ExitCode, declined.Said));
        Assert.Equal(JobRunner.Failed, broke.ExitCode);
        Assert.Contains("disk gone", broke.Said);
    }

    [Fact]
    public async Task Weigh_in_takes_the_same_reading_with_no_window_and_journals_it()
    {
        var drive = Path.Combine(_root, "drive");
        Directory.CreateDirectory(Path.Combine(drive, "games"));
        File.WriteAllBytes(Path.Combine(drive, "games", "big.bin"), new byte[4096]);
        _settings.SavePluginSettings("meows.weighin", new WeighInSettings { Drives = [drive], Depth = 1 });
        var store = new FakeHost.FakeStore();
        var host = new JobHost("meows.weighin", _settings, store, new ShellLog(Path.Combine(_root, "do.log")), _said.Add, (_, _, _) => { });

        var said = await new WeighInPlugin().RunJob(WeighInPlugin.MeasureJob, host, CancellationToken.None);

        Assert.Contains("1", said);
        Assert.Single(Readings.Load(Path.Combine(_settings.PluginDataDirectory("meows.weighin"), "readings")));
        Assert.Contains(store.Events, e => e.Kind == "reading" && e.Subject == drive);
        Assert.Contains(new WeighInPlugin().Jobs, j => j.Id == "measure" && j.StandsInForSchedule);
        await Assert.ThrowsAsync<JobDeclinedException>(() => new WeighInPlugin().RunJob("sweep", host, CancellationToken.None));
    }

    [Fact]
    public void A_toast_escapes_what_it_is_given_and_carries_its_buttons()
    {
        var xml = Toasts.ToastXml("Collar · <TÜV> & more", "due \"today\"", [("Done", "meows:act/abc123")]);
        var parsed = System.Xml.Linq.XDocument.Parse(xml);

        Assert.Equal(["Collar · <TÜV> & more", "due \"today\""], parsed.Descendants("text").Select(t => t.Value));
        var action = Assert.Single(parsed.Descendants("action"));
        Assert.Equal("meows:act/abc123", action.Attribute("arguments")!.Value);
        Assert.Equal("protocol", action.Attribute("activationType")!.Value);
    }

    [Fact]
    public void A_toast_button_is_pressed_once_through_its_argument()
    {
        var buttons = new ToastButtons();
        var pressed = 0;
        var argument = buttons.Remember(() => pressed++);

        Assert.StartsWith(Toasts.ActPrefix, argument);
        Assert.False(buttons.Press("meows:open"));
        Assert.True(buttons.Press(argument + "/"));
        Assert.False(buttons.Press(argument));
        Assert.Equal(1, pressed);
    }
}
