using System.IO.Compression;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Vet;
using Meows.Plugins.Vet.Services;
using Meows.Plugins.Vet.ViewModels;

namespace Meows.Tests;

/// <summary>
/// The checkup: drives read, reboot key asked, backup folder dated, verdicts in words.
/// </summary>
public class CheckupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "checkup-" + Guid.NewGuid().ToString("N")[..10]);

    public CheckupTests() => Directory.CreateDirectory(_root);

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

    private static IMeowsText Text() => TestStrings.Load();

    [Fact]
    public void A_drive_is_low_under_a_tenth_free_or_under_ten_gigabytes()
    {
        const long gb = 1024L * 1024 * 1024;
        Assert.True(new DriveRow { Name = "C:", TotalBytes = 100 * gb, FreeBytes = 9 * gb }.IsLow);
        Assert.True(new DriveRow { Name = "D:", TotalBytes = 2000 * gb, FreeBytes = 9 * gb }.IsLow);
        Assert.False(new DriveRow { Name = "E:", TotalBytes = 100 * gb, FreeBytes = 50 * gb }.IsLow);
        Assert.False(new DriveRow { Name = "?", TotalBytes = 0, FreeBytes = 0 }.IsLow);
    }

    [Fact]
    public void The_reboot_key_is_asked_without_throwing()
    {
        // Null anywhere but Windows, where there is no such key to read.
        if (!OperatingSystem.IsWindows())
            Assert.Null(Checkup.RebootNeeded());
        else
            _ = Checkup.RebootNeeded();
    }

    [Fact]
    public void Backup_age_is_the_newest_file_in_whole_days()
    {
        var backup = Path.Combine(_root, "backup");
        Directory.CreateDirectory(Path.Combine(backup, "deep"));
        var old = Path.Combine(backup, "old.txt");
        var now = Path.Combine(backup, "deep", "new.txt");
        File.WriteAllText(old, "x");
        File.WriteAllText(now, "x");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-9).Date);
        File.SetLastWriteTimeUtc(now, DateTime.UtcNow.AddDays(-2).Date);

        Assert.Equal(2, Checkup.BackupDays(backup, DateTime.UtcNow));
        Assert.Null(Checkup.BackupDays(null, DateTime.UtcNow));
        Assert.Null(Checkup.BackupDays(Path.Combine(_root, "missing"), DateTime.UtcNow));
        Assert.Null(Checkup.BackupDays(Path.Combine(_root, "empty"), DateTime.UtcNow));
    }

    [Fact]
    public void The_verdict_names_what_wants_doing_or_says_healthy()
    {
        var text = Text();
        var healthy = new CheckupSummary { Drives = 2 };

        Assert.Equal("2 drives, nothing wants doing", Checkup.SummaryOf(healthy, false, 7, text));
        Assert.False(Checkup.GlanceOf(healthy, false, 7, text)?.IsTrouble);
        Assert.Null(Checkup.GlanceOf(null, false, 7, text));

        var bad = new CheckupSummary
        {
            Drives = 2,
            LowDrives = ["C:"],
            RebootNeeded = true,
            BackupDays = 9,
        };
        var judged = Checkup.Judge(bad, true, 7, text);
        Assert.True(judged.IsTrouble);
        var line = Checkup.SummaryOf(bad, true, 7, text);
        Assert.Contains("C:", line);
        Assert.Contains("reboot", line);
        Assert.Contains("9", line);
    }

    [Fact]
    public void The_diagnostics_zip_holds_what_the_checkup_saw()
    {
        var text = Text();
        var drives = new List<DriveRow> { new() { Name = "C:", TotalBytes = 100, FreeBytes = 90 } };
        var summary = new CheckupSummary { AtUtc = DateTime.UtcNow, Drives = 1, RebootNeeded = false, BackupDays = 1 };
        var zip = Path.Combine(_root, "vet.zip");

        Checkup.WriteDiagnostics(zip, summary, true, 7, drives, text);

        using var archive = ZipFile.OpenRead(zip);
        var entry = Assert.Single(archive.Entries);
        Assert.Equal("checkup.txt", entry.Name);
        using var reader = new StreamReader(entry.Open());
        var body = reader.ReadToEnd();
        Assert.Contains("C:", body);
        Assert.Contains("nothing wants doing", body);
    }
}

/// <summary>
/// The tab itself: checkups run, conditions raised and cleared, diagnostics exported, and the
/// half that matters with no window.
/// </summary>
public sealed class VetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vet-" + Guid.NewGuid().ToString("N")[..10]);

    public VetTests()
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
    /// One roomy drive and no reboot waiting, whatever this PC is like: a nearly full drive here
    /// would otherwise keep the health condition up through every test that expects it gone.
    /// </summary>
    private static readonly MachineReadings Healthy = new(
        () => [new DriveRow { Name = "C:\\", TotalBytes = 500L << 30, FreeBytes = 250L << 30 }],
        () => false);

    [Fact]
    public void Opens_with_a_checkup_and_something_to_say()
    {
        using var model = new VetViewModel(Host("open"), Healthy);

        Assert.False(string.IsNullOrWhiteSpace(model.Status));
        Assert.False(model.HasError);
        Assert.False(string.IsNullOrWhiteSpace(model.Summary));
    }

    [Fact]
    public void A_stale_backup_is_raised_and_a_fresh_one_takes_it_down()
    {
        var host = Host("backup");
        var backup = Path.Combine(_root, "backup");
        Directory.CreateDirectory(backup);
        var file = Path.Combine(backup, "full.zip");
        File.WriteAllText(file, "x");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-9).Date);

        using var model = new VetViewModel(host, Healthy);
        host.Picks.Answers.Enqueue(backup);
        model.SetBackupCommand.Execute(null);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!model.HasBackup && DateTime.UtcNow < deadline)
            Thread.Sleep(50);
        Assert.True(model.HasBackup);
        Assert.Single(host.Conditions);
        Assert.Contains("backup 9 days old", model.Summary);

        File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
        model.CheckupCommand.Execute(null);
        Assert.DoesNotContain("backup", model.Summary);
        Assert.Empty(host.Conditions);
        Assert.Contains(host.Store.Events, e => e.Kind == "checked");
    }

    [Fact]
    public void A_full_drive_raises_the_condition_and_room_made_takes_it_down()
    {
        var free = 2L << 30;
        var machine = new MachineReadings(
            () => [new DriveRow { Name = "S:\\", TotalBytes = 500L << 30, FreeBytes = free }],
            () => false);
        var host = Host("full");

        using var model = new VetViewModel(host, machine);
        Assert.Single(host.Conditions);
        Assert.Contains("S:\\", model.Summary);

        free = 250L << 30;
        model.CheckupCommand.Execute(null);
        Assert.Empty(host.Conditions);
    }

    [Fact]
    public void Warn_days_fall_back_to_a_week_when_not_a_number()
    {
        using var model = new VetViewModel(Host("warn"), Healthy);

        model.WarnDaysText = "nonsense";

        Assert.Equal("7", model.WarnDaysText);
    }

    [Fact]
    public void Export_writes_a_zip_with_the_checkup_in_it()
    {
        var host = Host("export");
        var target = Path.Combine(_root, "vet.zip");
        host.Picks.Answers.Enqueue(target);

        using var model = new VetViewModel(host, Healthy);
        model.ExportCommand.Execute(null);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(target) && DateTime.UtcNow < deadline)
            Thread.Sleep(50);

        using var archive = ZipFile.OpenRead(target);
        Assert.Equal("checkup.txt", Assert.Single(archive.Entries).Name);
        Assert.Contains("written", model.Status);
    }

    [Fact]
    public void Search_reaches_into_the_drives_without_throwing()
    {
        using var model = new VetViewModel(Host("search"), Healthy);

        var hits = model.Search("zzz-no-such-drive", 5);

        Assert.Empty(hits);
        foreach (var drive in model.Drives)
        {
            // Substring matching is the house rule: the drive itself is among the hits, and
            // landing on its hit selects it.
            var hit = model.Search(drive.Name, 20).First(h => h.Title == drive.Name);
            hit.Open();
            Assert.Equal(drive.Name, model.Selected?.Name);
        }
    }

    [Fact]
    public void Glance_while_off_says_what_the_tab_would_say()
    {
        var inner = Host("glance-off");
        using (var model = new VetViewModel(inner, Healthy))
        {
        }

        var glance = new VetPlugin().GlanceWhileOff(new Dormant(inner, "meows.vet"));

        using var open = new VetViewModel(inner, Healthy);
        Assert.Equal(open.Glance(), glance);
    }

    [Fact]
    public async Task The_check_job_runs_and_reports()
    {
        var inner = Host("job");
        using (var model = new VetViewModel(inner, Healthy))
        {
        }

        Assert.Contains(new VetPlugin().Jobs, j => j.Id == VetPlugin.CheckJob);

        var jobHost = new JobHost(inner);
        var said = await new VetPlugin().RunJob(VetPlugin.CheckJob, jobHost, CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(said));
        Assert.Single(jobHost.Notices);

        await Assert.ThrowsAsync<JobDeclinedException>(() => new VetPlugin().RunJob("sweep", jobHost, CancellationToken.None));
    }

    /// <summary>The little a plugin gets while off: its settings.</summary>
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
        public string PluginId => "meows.vet";

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
