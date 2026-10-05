using Meows.Disk;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Screenshot;
using Meows.Plugins.Screenshot.Services;
using Meows.Plugins.Screenshot.ViewModels;

namespace Meows.Tests;

/// <summary>
/// Finding screenshots and sorting them, against a fake profile with every popular tool in it.
/// </summary>
public class ShotsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shots-" + Guid.NewGuid().ToString("N")[..10]);

    public ShotsTests() => Directory.CreateDirectory(_root);

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

    private string Profile(params string[] parts)
    {
        var folder = Path.Combine([_root, "profile", .. parts]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void Png(string path, byte fill, int size = 100)
    {
        var bytes = Enumerable.Repeat(fill, size).ToArray();
        File.WriteAllBytes(path, bytes);
    }

    private static void Manifest(string steamapps, string id, string name)
    {
        File.WriteAllLines(Path.Combine(steamapps, $"appmanifest_{id}.acf"),
        [
            "\"AppState\"", "{",
            $"\t\"appid\"\t\t\"{id}\"",
            $"\t\"name\"\t\t\"{name}\"",
            $"\t\"installdir\"\t\t\"{name.Replace(' ', '_')}\"",
            "\t\"SizeOnDisk\"\t\t\"1000\"",
        ]);
    }

    [Fact]
    public void The_usual_suspects_are_found_where_they_live()
    {
        var captures = Profile("Videos", "Captures");
        var windows = Profile("Pictures", "Screenshots");
        var videos = Path.Combine(_root, "profile", "Videos");
        var sharex = Profile("Documents", "ShareX", "Screenshots");
        var custom = Path.Combine(_root, "custom-shots");
        Directory.CreateDirectory(custom);
        File.WriteAllText(Path.Combine(_root, "profile", "Documents", "ShareX", "ApplicationConfig.json"),
            """{ "PersonalFolder": "" }""".Replace("\"\"", $"\"{custom.Replace("\\", "\\\\")}\""));
        var minecraft = Path.Combine(_root, "appdata", ".minecraft", "screenshots");
        Directory.CreateDirectory(minecraft);

        // One Steam library with a manifest and a per-game screenshots folder.
        var library = Path.Combine(_root, "steam");
        var steamapps = Path.Combine(library, "steamapps");
        Directory.CreateDirectory(steamapps);
        Manifest(steamapps, "70", "Half-Life");
        var hl = Path.Combine(library, "userdata", "123", "760", "remote", "70", "screenshots");
        Directory.CreateDirectory(hl);

        var found = Shots.FindSources(Path.Combine(_root, "profile"), Path.Combine(_root, "appdata"), [library]);

        Assert.Contains(found, f => f.Folder == captures && f.Label == "Game Bar");
        Assert.Contains(found, f => f.Folder == windows && f.Label == "Windows");
        Assert.Contains(found, f => f.Folder == videos && f.Label == "Videos");
        Assert.Contains(found, f => f.Folder == sharex && f.Label == "ShareX");
        Assert.Contains(found, f => f.Folder == custom && f.Label == "ShareX");
        Assert.Contains(found, f => f.Folder == minecraft && f.Label == "Minecraft");
        Assert.Contains(found, f => f.Folder == hl && f.Label == "Half-Life");
    }

    [Fact]
    public void What_does_not_exist_is_not_named_and_a_bad_config_is_not_a_source()
    {
        var found = Shots.FindSources(Path.Combine(_root, "nobody"), Path.Combine(_root, "nothing"), []);

        Assert.Empty(found);
        Assert.Empty(Shots.ShareXFolders(Path.Combine(_root, "nobody")));
    }

    [Fact]
    public void Merging_adds_new_switched_on_and_keeps_switches_and_nothing_is_dropped()
    {
        var kept = new List<ShotFolder> { new() { Path = @"F:\old", Label = "Old", Enabled = false } };

        var added = Shots.MergeFolders(kept,
            [( @"F:\old", "Old"), (@"F:\new", "New")]);

        Assert.Equal(1, added);
        Assert.Equal(2, kept.Count);
        Assert.False(kept.Single(f => f.Path == @"F:\old").Enabled);
        Assert.True(kept.Single(f => f.Path == @"F:\new").Enabled);
    }

    [Fact]
    public void Scanning_groups_newest_first_skips_recordings_and_counts_nested_once()
    {
        var captures = Profile("Videos", "Captures");
        Png(Path.Combine(captures, "a.png"), 1);
        Png(Path.Combine(captures, "b.jpg"), 2);
        File.WriteAllText(Path.Combine(captures, "clip.mp4"), "not a screenshot");

        var folders = new List<ShotFolder>
        {
            new() { Path = captures, Label = "Game Bar" },
            // Captures nests inside Videos: the same files must not count twice.
            new() { Path = Path.Combine(_root, "profile", "Videos"), Label = "Videos" },
        };

        var shots = Shots.Scan(folders);

        Assert.Equal(2, shots.Count);
        Assert.All(shots, s => Assert.Equal("Game Bar", s.Group));
    }

    [Fact]
    public void Identical_bytes_keep_the_newest_and_bursts_group_the_hasty()
    {
        var folder = Profile("Pictures", "Screenshots");
        var old = Path.Combine(folder, "old.png");
        var mid = Path.Combine(folder, "mid.png");
        var now = Path.Combine(folder, "now.png");
        var other = Path.Combine(folder, "other.png");
        Png(old, 7, 200);
        Png(mid, 7, 200);
        Png(now, 7, 200);
        Png(other, 8, 200);
        var base_ = new DateTime(2026, 10, 5, 20, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(old, base_);
        File.SetLastWriteTimeUtc(mid, base_.AddSeconds(5));
        File.SetLastWriteTimeUtc(now, base_.AddSeconds(200));
        File.SetLastWriteTimeUtc(other, base_.AddSeconds(6));

        var shots = Shots.Scan([new ShotFolder { Path = folder, Label = "Windows" }]);

        // Three identical: the newest stays, the two older are the copies.
        var victims = Shots.DuplicateVictims(shots);
        Assert.Equal(2, victims.Count);
        Assert.DoesNotContain(victims, v => v.Path == now);

        // old, mid and other are one burst of three; now stands alone.
        Assert.Equal(3, shots.Single(s => s.Path == old).Burst);
        Assert.Equal(3, shots.Single(s => s.Path == mid).Burst);
        Assert.Equal(3, shots.Single(s => s.Path == other).Burst);
        Assert.Equal(0, shots.Single(s => s.Path == now).Burst);
    }

    [Fact]
    public void Same_size_other_bytes_are_no_copies()
    {
        var folder = Profile("Videos", "Captures");
        Png(Path.Combine(folder, "a.png"), 1, 200);
        Png(Path.Combine(folder, "b.png"), 2, 200);

        var shots = Shots.Scan([new ShotFolder { Path = folder, Label = "Game Bar" }]);

        Assert.Empty(Shots.DuplicateVictims(shots));
    }

    [Fact]
    public void The_summary_names_shots_and_what_copies_hold()
    {
        var text = Text();

        Assert.Equal("", Shots.SummaryOf(null, text));
        Assert.Null(Shots.GlanceOf(null, text));

        var summary = new ShotSummary { Shots = 10, Bytes = 1024 * 1024, Duplicates = 2, DuplicateBytes = 2048 };
        Assert.Equal("10 shots, 1 MB. 2 copies holding 2 KB", Shots.SummaryOf(summary, text));
        Assert.True(Shots.GlanceOf(summary, text)?.IsTrouble);

        var clear = new ShotSummary { Shots = 3, Bytes = 100 };
        Assert.Equal("3 shots, 100 B", Shots.SummaryOf(clear, text));
        Assert.False(Shots.GlanceOf(clear, text)?.IsTrouble);
    }
}

/// <summary>
/// The tab itself: sources found on first open, keeps kept, copies recycled through the Bin,
/// best-of picks handed to Scruff, and the half that matters with no window.
/// </summary>
public sealed class ScreenshotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "screenshot-" + Guid.NewGuid().ToString("N")[..10]);

    public ScreenshotTests()
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

    private string Profile(params string[] parts)
    {
        var folder = Path.Combine([_root, "profile", .. parts]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void Png(string path, byte fill, int size = 100) =>
        File.WriteAllBytes(path, Enumerable.Repeat(fill, size).ToArray());

    private ScreenshotViewModel Model(FakeHost host, string profile) =>
        new(host, profile, Path.Combine(_root, "appdata"), () => []);

    [Fact]
    public void First_open_finds_the_usual_folders_and_says_so()
    {
        var captures = Profile("Videos", "Captures");
        Png(Path.Combine(captures, "a.png"), 1);
        var host = new FakeHost(Path.Combine(_root, "host-open"));

        using var model = Model(host, Path.Combine(_root, "profile"));

        Assert.Contains(model.Folders, f => f.Folder.Path == captures);
        Assert.False(string.IsNullOrWhiteSpace(model.Status));
        Assert.False(model.HasError);
    }

    [Fact]
    public void Refresh_reports_when_it_ran()
    {
        Profile("Videos", "Captures");
        var host = new FakeHost(Path.Combine(_root, "host-refresh"));

        using var model = Model(host, Path.Combine(_root, "profile"));

        model.RefreshCommand.Execute(null);

        Assert.Equal("Scanning…", model.Status);
    }

    [Fact]
    public void Shown_shots_group_duplicates_and_bursts_with_marks_that_survive()
    {
        var captures = Profile("Videos", "Captures");
        Png(Path.Combine(captures, "one.png"), 5, 200);
        Png(Path.Combine(captures, "two.png"), 5, 200);
        var host = new FakeHost(Path.Combine(_root, "host-shown"));

        using var model = Model(host, Path.Combine(_root, "profile"));
        model.Show(ScreenshotViewModel.RunScan(model.Folders.Select(f => f.Folder).ToList()));

        Assert.Equal(2, model.Rows.Count);
        Assert.Single(model.Rows, r => r.IsDuplicate);
        Assert.Contains("2 shots", model.Summary);

        // Tick the star: the pick survives the tab being closed and opened again.
        model.Selected = model.Rows.First(r => !r.IsDuplicate);
        model.Selected.Kept = true;

        using var again = Model(host, Path.Combine(_root, "profile"));
        again.Show(ScreenshotViewModel.RunScan(again.Folders.Select(f => f.Folder).ToList()));
        Assert.True(again.Rows.First(r => !r.IsDuplicate).Kept);
    }

    [Fact]
    public void Search_finds_a_shot_and_lands_on_it()
    {
        var captures = Profile("Videos", "Captures");
        Png(Path.Combine(captures, "elden-boss.png"), 3);
        var host = new FakeHost(Path.Combine(_root, "host-search"));

        using var model = Model(host, Path.Combine(_root, "profile"));
        model.Show(ScreenshotViewModel.RunScan(model.Folders.Select(f => f.Folder).ToList()));

        var hit = Assert.Single(model.Search("elden", 5));
        Assert.Equal("elden-boss.png", hit.Title);

        hit.Open();
        Assert.Equal("elden-boss.png", model.Selected?.Name);
    }

    [Fact]
    public void Keeps_go_to_scruff_when_it_is_there_and_nowhere_when_it_is_not()
    {
        var captures = Profile("Videos", "Captures");
        var file = Path.Combine(captures, "best.png");
        Png(file, 4);
        var host = new FakeHost(Path.Combine(_root, "host-send"));

        using var model = Model(host, Path.Combine(_root, "profile"));
        model.Show(ScreenshotViewModel.RunScan(model.Folders.Select(f => f.Folder).ToList()));
        model.Selected = model.Rows[0];
        model.Selected.Kept = true;

        // Nowhere to send: said out loud, nothing thrown.
        model.SendKeptCommand.Execute(null);
        Assert.NotNull(model.ErrorMessage);

        // Scruff installed: the keeps land on its pile and the sender hears nothing back to do.
        host.Handoffs.Reachable.Add(KnownPlugins.Scruff);
        Assert.True(model.CanReachScruff);
        model.SendKeptCommand.Execute(null);

        var (to, what) = Assert.Single(host.Handoffs.Sent);
        Assert.Equal(KnownPlugins.Scruff, to);
        Assert.Equal(HandoffVerbs.Files, what.Verb);
        Assert.Equal(file, Assert.Single(what.Paths));
        Assert.Null(model.ErrorMessage);
        Assert.Contains(host.Store.Events, e => e.Kind == "sent");
    }

    [Fact]
    public void While_off_answers_from_best_of_picks_and_a_hit_hands_it_to_itself()
    {
        var captures = Profile("Videos", "Captures");
        var file = Path.Combine(captures, "best.png");
        Png(file, 4);
        var inner = new FakeHost(Path.Combine(_root, "host-off"));

        using (var model = Model(inner, Path.Combine(_root, "profile")))
        {
            model.Show(ScreenshotViewModel.RunScan(model.Folders.Select(f => f.Folder).ToList()));
            model.Selected = model.Rows[0];
            model.Selected.Kept = true;
        }

        var dormant = new Dormant(inner, "meows.screenshot");
        dormant.Handoffs.Reachable.Add("meows.screenshot");
        var asleep = new ScreenshotPlugin().WhileOff(dormant);
        Assert.NotNull(asleep);

        var hit = Assert.Single(asleep.Search("best", 6));
        Assert.Equal("best.png", hit.Title);
        Assert.Equal("best-of pick", hit.Detail);

        hit.Open();
        var (to, what) = Assert.Single(dormant.Handoffs.Sent);
        Assert.Equal("meows.screenshot", to);
        Assert.Equal(ScreenshotPlugin.ShowVerb, what.Verb);
        Assert.Equal(file, what.Note);
    }

    [Fact]
    public void While_off_with_no_picks_has_nothing_to_search()
    {
        var dormant = new Dormant(new FakeHost(Path.Combine(_root, "host-empty-off")), "meows.screenshot");

        Assert.Null(new ScreenshotPlugin().WhileOff(dormant));
    }

    [Fact]
    public void Glance_while_off_says_what_the_tab_would_say()
    {
        var captures = Profile("Videos", "Captures");
        Png(Path.Combine(captures, "a.png"), 1);
        var inner = new FakeHost(Path.Combine(_root, "host-glance"));

        using (var model = Model(inner, Path.Combine(_root, "profile")))
            model.Show(ScreenshotViewModel.RunScan(model.Folders.Select(f => f.Folder).ToList()));

        var glance = new ScreenshotPlugin().GlanceWhileOff(new Dormant(inner, "meows.screenshot"));

        Assert.NotNull(glance);

        using var open = Model(inner, Path.Combine(_root, "profile"));
        Assert.Equal(open.Glance(), glance);
    }

    [Fact]
    public async Task The_scan_job_reads_and_says_what_the_library_holds()
    {
        var captures = Profile("Videos", "Captures");
        Png(Path.Combine(captures, "a.png"), 1);
        var inner = new FakeHost(Path.Combine(_root, "host-job"));

        using (var model = Model(inner, Path.Combine(_root, "profile")))
            model.Show(ScreenshotViewModel.RunScan(model.Folders.Select(f => f.Folder).ToList()));

        Assert.Contains(new ScreenshotPlugin().Jobs, j => j.Id == ScreenshotPlugin.ScanJob);

        var jobHost = new JobHost(inner);
        var said = await new ScreenshotPlugin().RunJob(ScreenshotPlugin.ScanJob, jobHost, CancellationToken.None);

        Assert.Contains("1 shots", said);
        Assert.Single(jobHost.Notices);

        await Assert.ThrowsAsync<JobDeclinedException>(() => new ScreenshotPlugin().RunJob("sweep", jobHost, CancellationToken.None));
    }

    [Fact]
    public async Task Recycling_the_copies_declines_where_the_bin_is_not_windows()
    {
        // The Bin is the Windows shell. Anywhere else the rule hears why, not an error.
        if (OperatingSystem.IsWindows())
            return;

        var captures = Profile("Videos", "Captures");
        Png(Path.Combine(captures, "a.png"), 9, 200);
        Png(Path.Combine(captures, "b.png"), 9, 200);
        var inner = new FakeHost(Path.Combine(_root, "host-rule"));

        using var model = Model(inner, Path.Combine(_root, "profile"));
        model.Show(ScreenshotViewModel.RunScan(model.Folders.Select(f => f.Folder).ToList()));
        Assert.Single(model.Rows, r => r.IsDuplicate);

        ActionRequest Asked(string action) =>
            new(action, new StoredEvent(7, DateTime.Now, "meows.birdwatch", "saved", "x", null,
                new Dictionary<string, string>()));

        await Assert.ThrowsAsync<ActionDeclinedException>(() => model.Perform(Asked(ScreenshotPlugin.RecycleAction), CancellationToken.None));
        await Assert.ThrowsAsync<ActionDeclinedException>(() => model.Perform(Asked("juggle"), CancellationToken.None));
    }

    [Fact]
    public void A_folder_can_be_added_by_hand()
    {
        Profile("Videos", "Captures");
        var host = new FakeHost(Path.Combine(_root, "host-add"));
        var extra = Path.Combine(_root, "extra");
        Directory.CreateDirectory(extra);
        host.Picks.Answers.Enqueue(extra);

        using var model = Model(host, Path.Combine(_root, "profile"));
        var before = model.Folders.Count;

        model.AddFolderCommand.Execute(null);

        // The pick dialog answers on its own thread; the folder lands shortly after.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (model.Folders.Count <= before && DateTime.UtcNow < deadline)
            Thread.Sleep(50);

        Assert.Contains(model.Folders, f => f.Folder.Path == extra);
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
        public string PluginId => "meows.screenshot";

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
