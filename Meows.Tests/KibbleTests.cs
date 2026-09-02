using Meows.Bot;
using Meows.Plugins.Kibble.Services;
using Meows.Plugins.Kibble.ViewModels;

namespace Meows.Tests;

public sealed class QueueRunwayTests
{
    private static GroupConfig Every(int minutes, int perPost = 1) => new()
    {
        Name = "G", ChatId = "-1", Folder = "groups/g",
        Schedule = new ScheduleConfig { IntervalMinutes = minutes },
        FilesPerPost = perPost,
    };

    [Fact]
    public void An_hourly_group_posts_24_a_day()
    {
        Assert.Equal(24, QueueRunway.FilesPerDay(Every(60)));
    }

    [Fact]
    public void Files_per_post_multiplies_the_rate()
    {
        Assert.Equal(48, QueueRunway.FilesPerDay(Every(60, perPost: 2)));
    }

    [Fact]
    public void A_daily_group_posts_its_batch_once()
    {
        var daily = new GroupConfig
        {
            Schedule = new ScheduleConfig { Hour = 9, Minute = 0 },
            FilesPerPost = 3,
        };

        Assert.Equal(3, QueueRunway.FilesPerDay(daily));
    }

    [Fact]
    public void Runway_is_what_makes_two_queues_comparable()
    {
        // The real case: 63 files sounds healthier than 481 until you divide by the rate.
        var hourly = QueueRunway.Days(Every(60), 63);
        var alsoHourly = QueueRunway.Days(Every(60), 481);

        Assert.Equal(2.6, hourly!.Value, 1);
        Assert.Equal(20.0, alsoHourly!.Value, 1);
    }

    [Fact]
    public void An_empty_queue_is_dry_rather_than_unknown()
    {
        Assert.Equal(0, QueueRunway.Days(Every(60), 0));
        Assert.Equal("dry", QueueRunway.Describe(0));
    }

    [Fact]
    public void A_disabled_group_has_no_runway_at_all()
    {
        var off = Every(60);
        off.Enabled = false;

        Assert.Null(QueueRunway.Days(off, 100));
        Assert.Equal("not scheduled", QueueRunway.Describe(null));
    }

    [Fact]
    public void Under_a_day_is_reported_in_hours()
    {
        Assert.Contains("hours", QueueRunway.Describe(0.5));
    }
}

public sealed class IntakeTests
{
    private static string WriteFile(string folder, string name, byte[]? content = null)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, content ?? [1, 2, 3, 4, 5, 6, 7, 8]);
        return path;
    }

    [Fact]
    public void Sending_moves_the_file_into_the_group_queue()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");
        var source = WriteFile(Path.Combine(temp.Root, "incoming"), "a.png");

        var result = Intake.Send(source, temp.Workspace, group, IntakeStamp.KeepSource);

        Assert.True(result.Moved);
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(result.Destination!));
        Assert.Equal(temp.Workspace.ToSendFolder(group), Path.GetDirectoryName(result.Destination));
    }

    [Fact]
    public void Keeping_the_source_time_preserves_posting_order()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");
        var source = WriteFile(Path.Combine(temp.Root, "incoming"), "old.png");
        var when = new DateTime(2020, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, when);

        var result = Intake.Send(source, temp.Workspace, group, IntakeStamp.KeepSource);

        Assert.Equal(when, File.GetLastWriteTimeUtc(result.Destination!));
    }

    [Fact]
    public void Stamping_on_intake_makes_it_first_in_first_out()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");
        var source = WriteFile(Path.Combine(temp.Root, "incoming"), "old.png");
        File.SetLastWriteTimeUtc(source, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var result = Intake.Send(source, temp.Workspace, group, IntakeStamp.QueuedNow);

        Assert.True(File.GetLastWriteTimeUtc(result.Destination!) > new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void A_file_already_in_the_queue_is_refused_and_left_alone()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");
        var payload = new byte[] { 9, 8, 7, 6, 5, 4, 3, 2 };
        temp.Queue(group, "already.png", DateTime.UtcNow, payload);
        var source = WriteFile(Path.Combine(temp.Root, "incoming"), "same-bytes.png", payload);

        var result = Intake.Send(source, temp.Workspace, group, IntakeStamp.KeepSource);

        Assert.Equal(IntakeOutcome.AlreadyInGroup, result.Outcome);
        Assert.True(File.Exists(source));
        Assert.Contains("queue", result.Detail!);
    }

    [Fact]
    public void A_file_already_posted_is_refused_too()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");
        var payload = new byte[] { 4, 4, 4, 4, 4, 4, 4, 4 };
        File.WriteAllBytes(Path.Combine(temp.Workspace.AlreadySentFolder(group), "sent.png"), payload);
        var source = WriteFile(Path.Combine(temp.Root, "incoming"), "again.png", payload);

        var result = Intake.Send(source, temp.Workspace, group, IntakeStamp.KeepSource);

        Assert.Equal(IntakeOutcome.AlreadyInGroup, result.Outcome);
        Assert.Contains("already posted", result.Detail!);
    }

    [Fact]
    public void A_duplicate_can_be_set_aside_instead_of_refused()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");
        var payload = new byte[] { 9, 8, 7, 6, 5, 4, 3, 2 };
        temp.Queue(group, "already.png", DateTime.UtcNow, payload);
        var source = WriteFile(Path.Combine(temp.Root, "incoming"), "same-bytes.png", payload);

        var result = Intake.Send(source, temp.Workspace, group, IntakeStamp.KeepSource,
            DuplicateHandling.MoveAside);

        Assert.Equal(IntakeOutcome.MovedToDuplicates, result.Outcome);
        Assert.False(File.Exists(source));
        Assert.Equal(temp.Workspace.DuplicatesFolder(group), Path.GetDirectoryName(result.Destination));
        Assert.True(File.Exists(result.Destination));
    }

    [Fact]
    public void Something_already_posted_is_set_aside_too()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");
        var payload = new byte[] { 4, 4, 4, 4, 4, 4, 4, 4 };
        File.WriteAllBytes(Path.Combine(temp.Workspace.AlreadySentFolder(group), "sent.png"), payload);
        var source = WriteFile(Path.Combine(temp.Root, "incoming"), "again.png", payload);

        var result = Intake.Send(source, temp.Workspace, group, IntakeStamp.KeepSource,
            DuplicateHandling.MoveAside);

        Assert.Equal(IntakeOutcome.MovedToDuplicates, result.Outcome);
        Assert.Contains("already posted", result.Detail!);
    }

    [Fact]
    public void A_duplicate_set_aside_never_reaches_the_queue()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");
        var payload = new byte[] { 1, 1, 2, 3, 5, 8, 13, 21 };
        temp.Queue(group, "already.png", DateTime.UtcNow, payload);
        var source = WriteFile(Path.Combine(temp.Root, "incoming"), "copy.png", payload);

        Intake.Send(source, temp.Workspace, group, IntakeStamp.KeepSource, DuplicateHandling.MoveAside);

        // The bot posts what is in To_Send. Setting a duplicate aside must not add to it, or the
        // feature would be posting the very thing it exists to hold back.
        var queue = Directory.GetFiles(temp.Workspace.ToSendFolder(group));
        Assert.Single(queue);
        Assert.EndsWith("already.png", queue[0]);
    }

    [Fact]
    public void Setting_aside_never_writes_over_a_duplicate_already_there()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");
        var payload = new byte[] { 7, 7, 7, 7, 7, 7, 7, 7 };
        temp.Queue(group, "already.png", DateTime.UtcNow, payload);

        var first = WriteFile(Path.Combine(temp.Root, "in1"), "copy.png", payload);
        var second = WriteFile(Path.Combine(temp.Root, "in2"), "copy.png", payload);

        var a = Intake.Send(first, temp.Workspace, group, IntakeStamp.KeepSource, DuplicateHandling.MoveAside);
        var b = Intake.Send(second, temp.Workspace, group, IntakeStamp.KeepSource, DuplicateHandling.MoveAside);

        // Two files of the same name from different folders. Finding a duplicate is a poor
        // reason to be careless with this copy of it.
        Assert.NotEqual(a.Destination, b.Destination);
        Assert.Equal(2, Directory.GetFiles(temp.Workspace.DuplicatesFolder(group)).Length);
    }

    [Fact]
    public void A_duplicate_set_aside_can_be_undone()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");
        var payload = new byte[] { 3, 1, 4, 1, 5, 9, 2, 6 };
        temp.Queue(group, "already.png", DateTime.UtcNow, payload);
        var source = WriteFile(Path.Combine(temp.Root, "incoming"), "copy.png", payload);

        var result = Intake.Send(source, temp.Workspace, group, IntakeStamp.KeepSource,
            DuplicateHandling.MoveAside);

        Assert.True(Intake.Undo(result));
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(result.Destination));
    }

    [Fact]
    public void Refusing_is_still_what_happens_when_it_is_not_asked_for()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");
        var payload = new byte[] { 2, 2, 2, 2, 2, 2, 2, 2 };
        temp.Queue(group, "already.png", DateTime.UtcNow, payload);
        var source = WriteFile(Path.Combine(temp.Root, "incoming"), "copy.png", payload);

        var result = Intake.Send(source, temp.Workspace, group, IntakeStamp.KeepSource);

        Assert.Equal(IntakeOutcome.AlreadyInGroup, result.Outcome);
        Assert.True(File.Exists(source));
        Assert.False(Directory.Exists(temp.Workspace.DuplicatesFolder(group)));
    }

    [Fact]
    public void A_batch_keeps_its_order_when_a_duplicate_is_pulled_out_of_it()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");
        var payload = new byte[] { 6, 6, 6, 6, 6, 6, 6, 6 };
        temp.Queue(group, "already.png", DateTime.UtcNow, payload);

        var incoming = Path.Combine(temp.Root, "incoming");
        var first = WriteFile(incoming, "a.png", [1, 1, 1, 1, 1, 1, 1, 1]);
        var duplicate = WriteFile(incoming, "b.png", payload);
        var last = WriteFile(incoming, "c.png", [3, 3, 3, 3, 3, 3, 3, 3]);

        var results = Intake.SendMany([first, duplicate, last], temp.Workspace, group,
            IntakeStamp.QueuedNow, DuplicateHandling.MoveAside);

        Assert.Equal(IntakeOutcome.MovedToDuplicates, results[1].Outcome);

        // The duplicate took no place in the posting order, so the two that were queued are
        // still a second apart rather than sharing a moment with the file that never joined them.
        var a = File.GetLastWriteTimeUtc(results[0].Destination!);
        var c = File.GetLastWriteTimeUtc(results[2].Destination!);
        Assert.Equal(1, Math.Round((c - a).TotalSeconds));
    }

    [Fact]
    public void The_same_image_may_go_to_a_second_group()
    {
        using var temp = new TempWorkspace();
        var a = temp.AddGroup("A");
        var b = temp.AddGroup("B", "-100222");
        var payload = new byte[] { 1, 1, 2, 2, 3, 3, 4, 4 };
        temp.Queue(a, "in-a.png", DateTime.UtcNow, payload);
        var source = WriteFile(Path.Combine(temp.Root, "incoming"), "for-b.png", payload);

        // Dedupe is per destination on purpose: two groups wanting the same picture is normal.
        var result = Intake.Send(source, temp.Workspace, b, IntakeStamp.KeepSource);

        Assert.True(result.Moved);
    }

    [Fact]
    public void A_type_the_bot_skips_is_refused()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");
        var source = WriteFile(Path.Combine(temp.Root, "incoming"), "notes.txt");

        var result = Intake.Send(source, temp.Workspace, group, IntakeStamp.KeepSource);

        Assert.Equal(IntakeOutcome.NotPostable, result.Outcome);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void A_comic_with_no_pages_is_refused_before_it_reaches_the_queue()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");
        var folder = Path.Combine(temp.Root, "incoming");
        Directory.CreateDirectory(folder);
        var zip = Path.Combine(folder, "empty.cbz");
        using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
            archive.CreateEntry("readme.txt");

        var result = Intake.Send(zip, temp.Workspace, group, IntakeStamp.KeepSource);

        Assert.Equal(IntakeOutcome.EmptyComic, result.Outcome);
        Assert.True(File.Exists(zip));
    }

    [Fact]
    public void A_name_clash_never_overwrites_what_is_already_queued()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");
        temp.Queue(group, "clash.png", DateTime.UtcNow, [1, 1, 1, 1]);
        var source = WriteFile(Path.Combine(temp.Root, "incoming"), "clash.png", [2, 2, 2, 2]);

        var result = Intake.Send(source, temp.Workspace, group, IntakeStamp.KeepSource);

        Assert.True(result.Moved);
        Assert.NotEqual("clash.png", Path.GetFileName(result.Destination));
        Assert.Equal(2, temp.Workspace.Scan(temp.Workspace.ToSendFolder(group)).Count);
    }

    [Fact]
    public void Undo_puts_the_file_back_where_it_came_from()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");
        var source = WriteFile(Path.Combine(temp.Root, "incoming"), "a.png");

        var result = Intake.Send(source, temp.Workspace, group, IntakeStamp.KeepSource);
        Assert.True(Intake.Undo(result));

        Assert.True(File.Exists(source));
        Assert.False(File.Exists(result.Destination!));
    }

    [Fact]
    public void Inspect_reports_the_same_problem_without_touching_anything()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");
        var source = WriteFile(Path.Combine(temp.Root, "incoming"), "notes.txt");

        var problem = Intake.Inspect(source, temp.Workspace, group);

        Assert.NotNull(problem);
        Assert.Equal(IntakeOutcome.NotPostable, problem.Outcome);
        Assert.True(File.Exists(source));
    }
}

public sealed class ContentHashTests
{
    [Fact]
    public void Identical_content_is_found_regardless_of_name()
    {
        using var temp = new TempWorkspace();
        var payload = new byte[4096];
        Random.Shared.NextBytes(payload);
        var folder = Path.Combine(temp.Root, "pool");
        Directory.CreateDirectory(folder);
        var a = Path.Combine(folder, "one.bin");
        var b = Path.Combine(folder, "two.bin");
        File.WriteAllBytes(a, payload);
        File.WriteAllBytes(b, payload);

        Assert.Equal(b, ContentHash.FindMatch(a, [b]));
    }

    [Fact]
    public void Same_size_different_bytes_is_not_a_match()
    {
        using var temp = new TempWorkspace();
        var folder = Path.Combine(temp.Root, "pool");
        Directory.CreateDirectory(folder);
        var a = Path.Combine(folder, "a.bin");
        var b = Path.Combine(folder, "b.bin");
        File.WriteAllBytes(a, new byte[4096]);
        var other = new byte[4096];
        other[4095] = 1;
        File.WriteAllBytes(b, other);

        Assert.Null(ContentHash.FindMatch(a, [b]));
    }

    [Fact]
    public void An_empty_candidate_list_is_no_match()
    {
        using var temp = new TempWorkspace();
        var p = Path.Combine(temp.Root, "x.bin");
        File.WriteAllBytes(p, [1, 2, 3]);

        Assert.Null(ContentHash.FindMatch(p, []));
    }
}

public sealed class ComicBundleTests
{
    private static string[] Pages(TempWorkspace temp, params string[] names)
    {
        var folder = Path.Combine(temp.Root, "incoming");
        Directory.CreateDirectory(folder);
        var paths = new List<string>();
        foreach (var name in names)
        {
            var path = Path.Combine(folder, name);
            // Distinct bytes per page, so a mixed up order is detectable.
            File.WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(name));
            paths.Add(path);
        }

        return paths.ToArray();
    }

    private static List<string> EntryNames(string archive)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(archive);
        return zip.Entries.Select(e => e.FullName).ToList();
    }

    [Fact]
    public void Picked_files_become_one_archive_in_the_queue()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");
        var pages = Pages(temp, "a.png", "b.png", "c.png");

        var result = Intake.SendAsComic(pages, temp.Workspace, group, IntakeStamp.KeepSource, "my set");

        Assert.True(result.Moved);
        Assert.True(result.IsBundle);
        Assert.Equal(".cbz", Path.GetExtension(result.Destination));
        Assert.Equal("my set.cbz", Path.GetFileName(result.Destination));
        // One queued item, not three.
        Assert.Single(temp.Workspace.Scan(temp.Workspace.ToSendFolder(group)));
        Assert.All(pages, p => Assert.False(File.Exists(p)));
    }

    [Fact]
    public void The_bot_can_read_every_page_back_out()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");
        var pages = Pages(temp, "1.png", "2.png", "3.png");

        var result = Intake.SendAsComic(pages, temp.Workspace, group, IntakeStamp.KeepSource, "set");

        Assert.Equal(3, MediaRules.ComicPages(result.Destination!).Count);
    }

    [Fact]
    public void Page_order_holds_under_every_comic_order_the_group_might_use()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");
        // Deliberately awkward: picked out of order, and numbered so a plain string sort
        // would put page10 before page2.
        var pages = Pages(temp, "page10.png", "page2.png", "page1.png");

        var result = Intake.SendAsComic(pages, temp.Workspace, group, IntakeStamp.KeepSource, "set");

        var expected = new[] { "page1.png", "page2.png", "page10.png" };
        foreach (var mode in new[] { "name", "date", "zip_order" })
        {
            var actual = MediaRules.ComicPages(result.Destination!, mode)
                .Select(n => n[(n.IndexOf('_') + 1)..])
                .ToArray();

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void An_index_prefix_is_what_makes_name_order_survive()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");
        var pages = Pages(temp, "zebra.png", "apple.png");

        var result = Intake.SendAsComic(pages, temp.Workspace, group, IntakeStamp.KeepSource, "set");

        Assert.Equal(new[] { "1_apple.png", "2_zebra.png" }, EntryNames(result.Destination!));
    }

    [Fact]
    public void A_gif_cannot_be_a_comic_page_and_nothing_is_moved()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");
        var pages = Pages(temp, "a.png", "loop.gif");

        var result = Intake.SendAsComic(pages, temp.Workspace, group, IntakeStamp.KeepSource, "set");

        Assert.Equal(IntakeOutcome.NotPostable, result.Outcome);
        Assert.Contains("loop.gif", result.Detail!);
        Assert.All(pages, p => Assert.True(File.Exists(p)));
        Assert.Empty(temp.Workspace.Scan(temp.Workspace.ToSendFolder(group)));
    }

    [Fact]
    public void One_file_is_not_a_comic()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");
        var pages = Pages(temp, "only.png");

        var result = Intake.SendAsComic(pages, temp.Workspace, group, IntakeStamp.KeepSource, "set");

        Assert.Equal(IntakeOutcome.NotPostable, result.Outcome);
        Assert.True(File.Exists(pages[0]));
    }

    [Fact]
    public void Keeping_the_source_date_takes_the_oldest_page()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");
        var pages = Pages(temp, "a.png", "b.png");
        var oldest = new DateTime(2019, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(pages[0], oldest);
        File.SetLastWriteTimeUtc(pages[1], new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var result = Intake.SendAsComic(pages, temp.Workspace, group, IntakeStamp.KeepSource, "set");

        // A comic is as old as the material in it, so the set posts in its rightful place.
        Assert.Equal(oldest, File.GetLastWriteTimeUtc(result.Destination!));
    }

    [Fact]
    public void Stamping_on_intake_dates_the_comic_now()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");
        var pages = Pages(temp, "a.png", "b.png");
        File.SetLastWriteTimeUtc(pages[0], new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var result = Intake.SendAsComic(pages, temp.Workspace, group, IntakeStamp.QueuedNow, "set");

        Assert.True(File.GetLastWriteTimeUtc(result.Destination!) > new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Undo_unpacks_the_comic_back_into_the_files_it_came_from()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");
        var pages = Pages(temp, "a.png", "b.png", "c.png");
        var when = new DateTime(2021, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        foreach (var page in pages)
            File.SetLastWriteTimeUtc(page, when);

        var result = Intake.SendAsComic(pages, temp.Workspace, group, IntakeStamp.KeepSource, "set");
        Assert.True(Intake.Undo(result));

        Assert.False(File.Exists(result.Destination!));
        foreach (var page in pages)
        {
            Assert.True(File.Exists(page));
            Assert.Equal(Path.GetFileName(page), File.ReadAllText(page));
            // The entry times inside the zip were synthetic, so the real one has to come back.
            Assert.Equal(when, File.GetLastWriteTimeUtc(page));
        }
    }

    [Fact]
    public void A_name_clash_does_not_overwrite_an_earlier_comic()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");

        var first = Intake.SendAsComic(Pages(temp, "a.png", "b.png"), temp.Workspace, group, IntakeStamp.KeepSource, "set");
        var second = Intake.SendAsComic(Pages(temp, "c.png", "d.png"), temp.Workspace, group, IntakeStamp.KeepSource, "set");

        Assert.True(first.Moved);
        Assert.True(second.Moved);
        Assert.NotEqual(first.Destination, second.Destination);
        Assert.Equal(2, temp.Workspace.Scan(temp.Workspace.ToSendFolder(group)).Count);
    }

    [Fact]
    public void An_empty_name_still_produces_a_usable_file()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");

        var result = Intake.SendAsComic(Pages(temp, "a.png", "b.png"), temp.Workspace, group, IntakeStamp.KeepSource, "   ");

        Assert.Equal("comic.cbz", Path.GetFileName(result.Destination));
    }

    [Fact]
    public void Characters_a_file_name_cannot_hold_are_dropped()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");

        var result = Intake.SendAsComic(Pages(temp, "a.png", "b.png"), temp.Workspace, group, IntakeStamp.KeepSource, "a/b:c*d");

        Assert.Equal("abcd.cbz", Path.GetFileName(result.Destination));
        Assert.True(File.Exists(result.Destination!));
    }
}

/// <summary>
/// The wiring between picking files in the grid and what gets queued. The grid is a ListBox so
/// ctrl and shift ranges are the control's job, but everything after "here is the selection" is
/// ours.
/// </summary>
public sealed class KibbleSelectionTests
{
    private static (KibbleViewModel Model, FakeHost Host, TempWorkspace Temp, string Folder) Open(params string[] names)
    {
        var temp = new TempWorkspace();
        temp.WriteConfig(temp.AddGroup("Alpha"), temp.AddGroup("Beta", "-100222"));

        var folder = Path.Combine(temp.Root, "intake");
        Directory.CreateDirectory(folder);
        foreach (var name in names)
            File.WriteAllBytes(Path.Combine(folder, name), System.Text.Encoding.UTF8.GetBytes(name));

        var host = new FakeHost(Path.Combine(temp.Root, "hostdata"));
        var model = new KibbleViewModel(host);
        model.SetBotRoot(temp.Workspace.Root);
        model.LoadFolder(folder);
        return (model, host, temp, folder);
    }

    [Fact]
    public void One_picked_file_is_a_plain_send_not_a_comic()
    {
        var (model, _, temp, _) = Open("a.png", "b.png");
        using var _t = temp;

        model.SetSelection([model.Incoming[0]]);

        Assert.False(model.IsBundle);
        Assert.Equal("SEND TO", model.SendVerb);
    }

    [Fact]
    public void Two_picked_files_turn_the_next_send_into_a_comic()
    {
        var (model, _, temp, _) = Open("a.png", "b.png");
        using var _t = temp;

        model.SetSelection([model.Incoming[0], model.Incoming[1]]);

        Assert.True(model.IsBundle);
        Assert.Equal("SEND AS ONE COMIC", model.SendVerb);
        Assert.Equal("2 picked", model.SelectionText);
    }

    [Fact]
    public void Sending_a_pick_of_three_queues_one_comic_and_empties_the_grid()
    {
        var (model, _, temp, _) = Open("page1.png", "page2.png", "page10.png");
        using var _t = temp;

        model.SetSelection([.. model.Incoming]);
        var alpha = model.Destinations.First(d => d.Name == "Alpha");
        model.SendToCommand.Execute(alpha);

        var queued = temp.Workspace.Scan(temp.Workspace.ToSendFolder(alpha.Group));
        Assert.Single(queued);
        Assert.Equal(".cbz", Path.GetExtension(queued[0]));
        Assert.Equal(3, MediaRules.ComicPages(queued[0]).Count);
        Assert.Empty(model.Incoming);
        Assert.False(model.IsBundle);
    }

    [Fact]
    public void A_number_key_sends_the_pick_to_that_destination()
    {
        var (model, _, temp, _) = Open("a.png", "b.png", "c.png");
        using var _t = temp;

        model.SetSelection([model.Incoming[0], model.Incoming[1]]);
        // Same path the 1 to 9 keys take: an index rather than a clicked object.
        model.SendToCommand.Execute(1);

        var first = model.Destinations[0];
        Assert.Single(temp.Workspace.Scan(temp.Workspace.ToSendFolder(first.Group)));
        Assert.Single(model.Incoming);
    }

    [Fact]
    public void A_single_pick_still_moves_the_file_itself()
    {
        var (model, _, temp, _) = Open("a.png", "b.png");
        using var _t = temp;

        model.SetSelection([model.Incoming[0]]);
        var alpha = model.Destinations.First(d => d.Name == "Alpha");
        model.SendToCommand.Execute(alpha);

        var queued = temp.Workspace.Scan(temp.Workspace.ToSendFolder(alpha.Group));
        Assert.Single(queued);
        Assert.Equal("a.png", Path.GetFileName(queued[0]));
    }

    [Fact]
    public void A_pick_containing_something_that_cannot_be_a_page_is_refused_and_kept()
    {
        var (model, _, temp, _) = Open("a.png", "loop.gif");
        using var _t = temp;

        model.SetSelection([.. model.Incoming]);
        var alpha = model.Destinations.First(d => d.Name == "Alpha");
        model.SendToCommand.Execute(alpha);

        Assert.True(model.IsBlocked);
        Assert.Contains("loop.gif", model.BlockedReason!);
        Assert.Equal(2, model.Incoming.Count);
        Assert.Empty(temp.Workspace.Scan(temp.Workspace.ToSendFolder(alpha.Group)));
    }

    [Fact]
    public void Undo_puts_every_page_of_a_comic_back_in_the_grid()
    {
        var (model, _, temp, _) = Open("a.png", "b.png", "c.png");
        using var _t = temp;

        model.SetSelection([model.Incoming[0], model.Incoming[1]]);
        var alpha = model.Destinations.First(d => d.Name == "Alpha");
        model.SendToCommand.Execute(alpha);
        Assert.Single(model.Incoming);

        model.UndoCommand.Execute(null);

        Assert.Equal(3, model.Incoming.Count);
        Assert.Empty(temp.Workspace.Scan(temp.Workspace.ToSendFolder(alpha.Group)));
    }

    [Fact]
    public void The_comic_is_named_after_the_folder_by_default()
    {
        var (model, _, temp, folder) = Open("a.png", "b.png");
        using var _t = temp;

        Assert.Equal(Path.GetFileName(folder), model.ArchiveName);

        model.SetSelection([.. model.Incoming]);
        var alpha = model.Destinations.First(d => d.Name == "Alpha");
        model.SendToCommand.Execute(alpha);

        var queued = temp.Workspace.Scan(temp.Workspace.ToSendFolder(alpha.Group));
        Assert.Equal($"{Path.GetFileName(folder)}.cbz", Path.GetFileName(queued[0]));
    }
}

/// <summary>How the folder you opened is laid out, and which page each picked file becomes.</summary>
public sealed class KibbleSortAndPageOrderTests
{
    private static (KibbleViewModel Model, TempWorkspace Temp) Open(params (string Name, int DaysOld)[] files)
    {
        var temp = new TempWorkspace();
        temp.WriteConfig(temp.AddGroup("Alpha"));

        var folder = Path.Combine(temp.Root, "intake");
        Directory.CreateDirectory(folder);
        foreach (var (name, daysOld) in files)
        {
            var path = Path.Combine(folder, name);
            File.WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(name));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-daysOld));
        }

        var model = new KibbleViewModel(new FakeHost(Path.Combine(temp.Root, "hostdata")));
        model.SetBotRoot(temp.Workspace.Root);
        model.LoadFolder(folder);
        return (model, temp);
    }

    private static string[] Names(KibbleViewModel m) => m.Incoming.Select(f => f.FileName).ToArray();

    private static void Sort(KibbleViewModel m, GridSort sort) =>
        m.SelectedSort = m.SortOptions.First(o => o.Value == sort);

    [Fact]
    public void Name_order_is_natural_so_page2_beats_page10()
    {
        var (m, temp) = Open(("page10.png", 1), ("page2.png", 2), ("page1.png", 3));
        using var _t = temp;

        Assert.Equal(new[] { "page1.png", "page2.png", "page10.png" }, Names(m));
    }

    [Fact]
    public void Name_descending_turns_it_around()
    {
        var (m, temp) = Open(("page10.png", 1), ("page2.png", 2), ("page1.png", 3));
        using var _t = temp;

        Sort(m, GridSort.NameDescending);

        Assert.Equal(new[] { "page10.png", "page2.png", "page1.png" }, Names(m));
    }

    [Fact]
    public void Newest_and_oldest_use_the_modified_time_not_the_name()
    {
        var (m, temp) = Open(("a.png", 10), ("b.png", 1), ("c.png", 5));
        using var _t = temp;

        Sort(m, GridSort.NewestFirst);
        Assert.Equal(new[] { "b.png", "c.png", "a.png" }, Names(m));

        Sort(m, GridSort.OldestFirst);
        Assert.Equal(new[] { "a.png", "c.png", "b.png" }, Names(m));
    }

    [Fact]
    public void Sorting_reorders_what_is_loaded_rather_than_losing_it()
    {
        var (m, temp) = Open(("a.png", 3), ("b.png", 2), ("c.png", 1));
        using var _t = temp;
        var before = m.Incoming.ToList();

        Sort(m, GridSort.NewestFirst);

        // Same view model instances, so a decoded thumbnail survives a reorder.
        Assert.Equal(3, m.Incoming.Count);
        Assert.All(m.Incoming, f => Assert.Contains(f, before));
    }

    [Fact]
    public void The_sort_is_remembered()
    {
        var (m, temp) = Open(("a.png", 1), ("b.png", 2));
        using var _t = temp;

        Sort(m, GridSort.OldestFirst);

        Assert.Equal(GridSort.OldestFirst, m.SelectedSort.Value);
    }

    [Fact]
    public void Picking_numbers_the_tiles_in_the_order_they_will_be_pages()
    {
        var (m, temp) = Open(("page10.png", 1), ("page2.png", 2), ("page1.png", 3));
        using var _t = temp;

        m.SetSelection([.. m.Incoming]);

        // Grid is page1, page2, page10 and by name that is also the page order.
        Assert.Equal(1, m.Incoming[0].PageNumber);
        Assert.Equal(2, m.Incoming[1].PageNumber);
        Assert.Equal(3, m.Incoming[2].PageNumber);
    }

    [Fact]
    public void A_single_pick_carries_no_page_number()
    {
        var (m, temp) = Open(("a.png", 1), ("b.png", 2));
        using var _t = temp;

        m.SetSelection([m.Incoming[0]]);

        Assert.All(m.Incoming, f => Assert.Equal(0, f.PageNumber));
        Assert.False(m.Incoming[0].HasPageNumber);
    }

    [Fact]
    public void In_pick_order_the_numbers_follow_the_clicks_not_the_names()
    {
        var (m, temp) = Open(("a.png", 1), ("b.png", 2), ("c.png", 3));
        using var _t = temp;
        m.SelectedPageOrder = m.PageOrderOptions.First(o => o.Value == PageOrder.AsPicked);

        var a = m.Incoming.First(f => f.FileName == "a.png");
        var b = m.Incoming.First(f => f.FileName == "b.png");
        var c = m.Incoming.First(f => f.FileName == "c.png");
        m.SetSelection([c, a, b]);

        Assert.Equal(1, c.PageNumber);
        Assert.Equal(2, a.PageNumber);
        Assert.Equal(3, b.PageNumber);
    }

    [Fact]
    public void Adding_one_more_does_not_renumber_what_was_already_picked()
    {
        var (m, temp) = Open(("a.png", 1), ("b.png", 2), ("c.png", 3));
        using var _t = temp;
        m.SelectedPageOrder = m.PageOrderOptions.First(o => o.Value == PageOrder.AsPicked);

        var a = m.Incoming.First(f => f.FileName == "a.png");
        var b = m.Incoming.First(f => f.FileName == "b.png");
        var c = m.Incoming.First(f => f.FileName == "c.png");
        m.SetSelection([c, a]);
        // The list control hands the set back in its own order, which must not reshuffle pages.
        m.SetSelection([a, b, c]);

        Assert.Equal(1, c.PageNumber);
        Assert.Equal(2, a.PageNumber);
        Assert.Equal(3, b.PageNumber);
    }

    [Fact]
    public void The_archive_is_written_in_the_order_the_numbers_promised()
    {
        var (m, temp) = Open(("a.png", 1), ("b.png", 2), ("c.png", 3));
        using var _t = temp;
        m.SelectedPageOrder = m.PageOrderOptions.First(o => o.Value == PageOrder.AsPicked);

        var a = m.Incoming.First(f => f.FileName == "a.png");
        var b = m.Incoming.First(f => f.FileName == "b.png");
        var c = m.Incoming.First(f => f.FileName == "c.png");
        m.SetSelection([c, a, b]);
        m.SendToCommand.Execute(m.Destinations[0]);

        var queued = temp.Workspace.Scan(temp.Workspace.ToSendFolder(m.Destinations[0].Group));
        var pages = MediaRules.ComicPages(queued[0]).Select(n => n[(n.IndexOf('_') + 1)..]).ToArray();
        Assert.Equal(new[] { "c.png", "a.png", "b.png" }, pages);
    }

    [Fact]
    public void Sorting_clears_the_pick_so_stale_numbers_cannot_linger()
    {
        var (m, temp) = Open(("a.png", 1), ("b.png", 2), ("c.png", 3));
        using var _t = temp;
        m.SetSelection([.. m.Incoming]);
        Assert.True(m.IsBundle);

        Sort(m, GridSort.NewestFirst);

        Assert.False(m.IsBundle);
        Assert.All(m.Incoming, f => Assert.Equal(0, f.PageNumber));
    }
}

public sealed class ComicPageOrderTests
{
    [Fact]
    public void As_picked_leaves_the_order_alone()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");
        var folder = Path.Combine(temp.Root, "in");
        Directory.CreateDirectory(folder);
        var paths = new List<string>();
        foreach (var n in new[] { "zebra.png", "apple.png", "mango.png" })
        {
            var path = Path.Combine(folder, n);
            File.WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(n));
            paths.Add(path);
        }

        var result = Intake.SendAsComic(paths, temp.Workspace, group, IntakeStamp.KeepSource, "set", PageOrder.AsPicked);

        Assert.Equal(
            new[] { "1_zebra.png", "2_apple.png", "3_mango.png" },
            MediaRules.ComicPages(result.Destination!, "zip_order").ToArray());
    }

    [Fact]
    public void As_picked_still_survives_every_comic_order_mode()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");
        var folder = Path.Combine(temp.Root, "in");
        Directory.CreateDirectory(folder);
        var paths = new List<string>();
        foreach (var n in new[] { "zebra.png", "apple.png", "mango.png" })
        {
            var path = Path.Combine(folder, n);
            File.WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(n));
            paths.Add(path);
        }

        var result = Intake.SendAsComic(paths, temp.Workspace, group, IntakeStamp.KeepSource, "set", PageOrder.AsPicked);

        var expected = new[] { "zebra.png", "apple.png", "mango.png" };
        foreach (var mode in new[] { "name", "date", "zip_order" })
        {
            var actual = MediaRules.ComicPages(result.Destination!, mode)
                .Select(n => n[(n.IndexOf('_') + 1)..])
                .ToArray();
            Assert.Equal(expected, actual);
        }
    }
}

/// <summary>Sending a pick in as separate files rather than as one comic.</summary>
public sealed class KibbleFileModeTests
{
    private static (KibbleViewModel Model, TempWorkspace Temp) Open(params string[] names)
    {
        var temp = new TempWorkspace();
        temp.WriteConfig(temp.AddGroup("Alpha"));

        var folder = Path.Combine(temp.Root, "intake");
        Directory.CreateDirectory(folder);
        foreach (var name in names)
            File.WriteAllBytes(Path.Combine(folder, name), System.Text.Encoding.UTF8.GetBytes(name));

        var model = new KibbleViewModel(new FakeHost(Path.Combine(temp.Root, "hostdata")));
        model.SetBotRoot(temp.Workspace.Root);
        model.LoadFolder(folder);
        model.BundleMode = BundleMode.AsFiles;
        return (model, temp);
    }

    [Fact]
    public void The_pick_arrives_as_separate_files_not_one_archive()
    {
        var (m, temp) = Open("a.png", "b.png", "c.png");
        using var _t = temp;

        m.SetSelection([.. m.Incoming]);
        m.SendToCommand.Execute(m.Destinations[0]);

        var queued = temp.Workspace.Scan(temp.Workspace.ToSendFolder(m.Destinations[0].Group));
        Assert.Equal(3, queued.Count);
        Assert.All(queued, q => Assert.NotEqual(".cbz", Path.GetExtension(q)));
        Assert.Equal(
            new[] { "a.png", "b.png", "c.png" },
            queued.Select(Path.GetFileName).OrderBy(x => x).ToArray());
        Assert.Empty(m.Incoming);
    }

    [Fact]
    public void Comic_mode_still_produces_one_archive()
    {
        var (m, temp) = Open("a.png", "b.png");
        using var _t = temp;
        m.BundleMode = BundleMode.AsComic;

        m.SetSelection([.. m.Incoming]);
        m.SendToCommand.Execute(m.Destinations[0]);

        var queued = temp.Workspace.Scan(temp.Workspace.ToSendFolder(m.Destinations[0].Group));
        Assert.Single(queued);
        Assert.Equal(".cbz", Path.GetExtension(queued[0]));
    }

    [Fact]
    public void File_mode_shows_no_page_numbers()
    {
        var (m, temp) = Open("a.png", "b.png");
        using var _t = temp;

        m.SetSelection([.. m.Incoming]);

        Assert.True(m.IsBundle);
        Assert.All(m.Incoming, f => Assert.Equal(0, f.PageNumber));
    }

    [Fact]
    public void Switching_to_comic_mode_brings_the_numbers_back()
    {
        var (m, temp) = Open("a.png", "b.png");
        using var _t = temp;
        m.SetSelection([.. m.Incoming]);
        Assert.All(m.Incoming, f => Assert.Equal(0, f.PageNumber));

        m.BundleMode = BundleMode.AsComic;

        Assert.Equal(1, m.Incoming[0].PageNumber);
        Assert.Equal(2, m.Incoming[1].PageNumber);
    }

    [Fact]
    public void A_refused_file_is_kept_and_the_rest_still_go()
    {
        var (m, temp) = Open("a.png", "notes.txt", "b.png");
        using var _t = temp;

        m.SetSelection([.. m.Incoming]);
        m.SendToCommand.Execute(m.Destinations[0]);

        // The txt is not something the bot posts, so it stays behind on its own.
        Assert.Equal(2, temp.Workspace.Scan(temp.Workspace.ToSendFolder(m.Destinations[0].Group)).Count);
        Assert.Single(m.Incoming);
        Assert.Equal("notes.txt", m.Incoming[0].FileName);
        Assert.True(m.IsBlocked);
        Assert.Contains("notes.txt", m.BlockedReason!);
    }

    [Fact]
    public void A_gif_is_fine_as_a_file_even_though_it_cannot_be_a_page()
    {
        var (m, temp) = Open("a.png", "loop.gif");
        using var _t = temp;

        m.SetSelection([.. m.Incoming]);
        m.SendToCommand.Execute(m.Destinations[0]);

        // The comic path refuses a gif. Sending it as itself is exactly what it is for.
        Assert.Equal(2, temp.Workspace.Scan(temp.Workspace.ToSendFolder(m.Destinations[0].Group)).Count);
        Assert.Empty(m.Incoming);
    }

    [Fact]
    public void Undo_puts_the_whole_batch_back()
    {
        var (m, temp) = Open("a.png", "b.png", "c.png");
        using var _t = temp;

        m.SetSelection([.. m.Incoming]);
        m.SendToCommand.Execute(m.Destinations[0]);
        Assert.Empty(m.Incoming);

        m.UndoCommand.Execute(null);

        Assert.Equal(3, m.Incoming.Count);
        Assert.Empty(temp.Workspace.Scan(temp.Workspace.ToSendFolder(m.Destinations[0].Group)));
    }

    [Fact]
    public void The_two_buttons_under_the_groups_switch_the_mode()
    {
        var (m, temp) = Open("a.png", "b.png");
        using var _t = temp;

        // Exactly what the buttons are bound to.
        m.ChooseComicCommand.Execute(null);
        Assert.True(m.IsComicMode);
        Assert.False(m.IsFileMode);

        m.ChooseFilesCommand.Execute(null);
        Assert.True(m.IsFileMode);
        Assert.False(m.IsComicMode);
    }

    [Fact]
    public void The_heading_says_which_mode_is_armed_before_you_commit()
    {
        var (m, temp) = Open("a.png", "b.png", "c.png");
        using var _t = temp;
        m.SetSelection([.. m.Incoming]);

        m.ChooseFilesCommand.Execute(null);
        Assert.Equal("SEND 3 FILES", m.SendVerb);
        Assert.Contains("in the order shown", m.BundleText);

        m.ChooseComicCommand.Execute(null);
        Assert.Equal("SEND AS ONE COMIC", m.SendVerb);
        Assert.Contains("zipped into one comic", m.BundleText);
    }

    [Fact]
    public void The_mode_is_remembered()
    {
        var (m, temp) = Open("a.png", "b.png");
        using var _t = temp;

        Assert.True(m.IsFileMode);
        Assert.False(m.IsComicMode);
        Assert.Contains("SEND", m.SendVerb);
    }
}

public sealed class SendManyTests
{
    private static string[] Write(TempWorkspace temp, params string[] names)
    {
        var folder = Path.Combine(temp.Root, "in");
        Directory.CreateDirectory(folder);
        var paths = new List<string>();
        foreach (var n in names)
        {
            var path = Path.Combine(folder, n);
            File.WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(n));
            paths.Add(path);
        }

        return paths.ToArray();
    }

    [Fact]
    public void Stamping_on_intake_keeps_the_order_they_were_sent_in()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");
        var files = Write(temp, "third.png", "first.png", "second.png");

        var results = Intake.SendMany(files, temp.Workspace, group, IntakeStamp.QueuedNow);

        // The bot posts oldest first, so the times have to ascend in the order given, not in
        // name order and not all at once.
        var times = results.Select(r => File.GetLastWriteTimeUtc(r.Destination!)).ToList();
        Assert.True(times[0] < times[1], "first sent should be oldest");
        Assert.True(times[1] < times[2], "second sent should come before third");
    }

    [Fact]
    public void Keeping_the_source_date_leaves_each_file_alone()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");
        var files = Write(temp, "a.png", "b.png");
        var when = new DateTime(2020, 6, 7, 8, 9, 10, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(files[0], when);

        var results = Intake.SendMany(files, temp.Workspace, group, IntakeStamp.KeepSource);

        Assert.Equal(when, File.GetLastWriteTimeUtc(results[0].Destination!));
    }

    [Fact]
    public void One_refusal_does_not_stop_the_others()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");
        var files = Write(temp, "a.png", "notes.txt", "b.png");

        var results = Intake.SendMany(files, temp.Workspace, group, IntakeStamp.KeepSource);

        Assert.True(results[0].Moved);
        Assert.Equal(IntakeOutcome.NotPostable, results[1].Outcome);
        Assert.True(results[2].Moved);
        Assert.True(File.Exists(files[1]));
    }

    [Fact]
    public void Results_come_back_in_the_order_they_went_in()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");
        var files = Write(temp, "z.png", "y.png", "x.png");

        var results = Intake.SendMany(files, temp.Workspace, group, IntakeStamp.KeepSource);

        Assert.Equal(files, results.Select(r => r.SourcePath).ToArray());
    }
}

/// <summary>
/// Loading a big folder in batches. A folder of thousands should cost a list of small records,
/// not thousands of tiles each with a decoded thumbnail.
/// </summary>
public sealed class KibbleLazyLoadTests
{
    private static (KibbleViewModel Model, TempWorkspace Temp) Open(int fileCount, int pageSize = 10, bool lazy = true)
    {
        var temp = new TempWorkspace();
        temp.WriteConfig(temp.AddGroup("Alpha"));

        var folder = Path.Combine(temp.Root, "intake");
        Directory.CreateDirectory(folder);
        for (var i = 1; i <= fileCount; i++)
        {
            var path = Path.Combine(folder, $"f{i:D4}.png");
            File.WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes($"f{i}"));
            File.SetLastWriteTimeUtc(path, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i));
        }

        var model = new KibbleViewModel(new FakeHost(Path.Combine(temp.Root, "hostdata")));
        model.SetBotRoot(temp.Workspace.Root);
        model.PageSize = pageSize;
        model.LazyLoad = lazy;
        model.LoadFolder(folder);
        return (model, temp);
    }

    [Fact]
    public void Only_one_batch_is_built_at_first()
    {
        var (m, temp) = Open(fileCount: 50, pageSize: 10);
        using var _t = temp;

        Assert.Equal(10, m.Incoming.Count);
        Assert.True(m.HasMore);
        // The count still tells the truth about the whole folder.
        Assert.Equal("50 files left", m.RemainingText);
    }

    [Fact]
    public void Off_by_default_it_builds_the_lot()
    {
        var (m, temp) = Open(fileCount: 50, lazy: false);
        using var _t = temp;

        Assert.Equal(50, m.Incoming.Count);
        Assert.False(m.HasMore);
    }

    [Fact]
    public void Load_more_adds_another_batch()
    {
        var (m, temp) = Open(fileCount: 50, pageSize: 10);
        using var _t = temp;

        m.LoadMoreCommand.Execute(null);
        Assert.Equal(20, m.Incoming.Count);

        m.LoadMoreCommand.Execute(null);
        Assert.Equal(30, m.Incoming.Count);
    }

    [Fact]
    public void Load_more_stops_at_the_end_and_the_button_goes_away()
    {
        var (m, temp) = Open(fileCount: 12, pageSize: 10);
        using var _t = temp;

        m.LoadMoreCommand.Execute(null);

        Assert.Equal(12, m.Incoming.Count);
        Assert.False(m.HasMore);
        Assert.False(m.LoadMoreCommand.CanExecute(null));
    }

    [Fact]
    public void Sending_tops_the_batch_back_up_from_what_is_waiting()
    {
        var (m, temp) = Open(fileCount: 50, pageSize: 10);
        using var _t = temp;
        var first = m.Incoming[0];

        m.SetSelection([first]);
        m.SendToCommand.Execute(m.Destinations[0]);

        // Still a full batch on screen, one fewer waiting overall.
        Assert.Equal(10, m.Incoming.Count);
        Assert.Equal("49 files left", m.RemainingText);
        Assert.DoesNotContain(m.Incoming, f => f.FileName == first.FileName);
    }

    [Fact]
    public void A_comic_of_a_whole_batch_pulls_the_next_batch_in()
    {
        var (m, temp) = Open(fileCount: 50, pageSize: 10);
        using var _t = temp;
        var sent = m.Incoming.Select(f => f.FileName).ToList();

        m.SetSelection([.. m.Incoming]);
        m.SendToCommand.Execute(m.Destinations[0]);

        Assert.Equal(10, m.Incoming.Count);
        Assert.Equal("40 files left", m.RemainingText);
        Assert.All(m.Incoming, f => Assert.DoesNotContain(f.FileName, sent));
    }

    [Fact]
    public void Sorting_orders_the_whole_folder_not_just_the_batch()
    {
        var (m, temp) = Open(fileCount: 50, pageSize: 10);
        using var _t = temp;
        Assert.Equal("f0001.png", m.Incoming[0].FileName);

        m.SelectedSort = m.SortOptions.First(o => o.Value == GridSort.NewestFirst);

        // f0050 is the newest of all fifty, not merely of the ten that happened to be built.
        Assert.Equal("f0050.png", m.Incoming[0].FileName);
        Assert.Equal(10, m.Incoming.Count);
    }

    [Fact]
    public void Turning_it_off_builds_everything_that_is_left()
    {
        var (m, temp) = Open(fileCount: 50, pageSize: 10);
        using var _t = temp;
        Assert.Equal(10, m.Incoming.Count);

        m.LazyLoad = false;

        Assert.Equal(50, m.Incoming.Count);
        Assert.False(m.HasMore);
    }

    [Fact]
    public void Changing_the_batch_size_takes_effect_at_once()
    {
        var (m, temp) = Open(fileCount: 50, pageSize: 10);
        using var _t = temp;

        m.PageSize = 25;

        Assert.Equal(25, m.Incoming.Count);
    }

    [Fact]
    public void Undo_brings_a_file_back_even_when_it_sorts_outside_the_batch()
    {
        var (m, temp) = Open(fileCount: 50, pageSize: 10);
        using var _t = temp;

        // Send the very first file, then put it back. It belongs at the top, so it should be
        // on screen again rather than lost somewhere past the end of the batch.
        var first = m.Incoming[0];
        var name = first.FileName;
        m.SetSelection([first]);
        m.SendToCommand.Execute(m.Destinations[0]);
        Assert.DoesNotContain(m.Incoming, f => f.FileName == name);

        m.UndoCommand.Execute(null);

        Assert.Contains(m.Incoming, f => f.FileName == name);
        Assert.Equal("50 files left", m.RemainingText);
    }

    [Fact]
    public void A_thumbnail_already_decoded_is_not_thrown_away_by_a_sort()
    {
        var (m, temp) = Open(fileCount: 50, pageSize: 10);
        using var _t = temp;
        var kept = m.Incoming.First(f => f.FileName == "f0001.png");

        m.SelectedSort = m.SortOptions.First(o => o.Value == GridSort.OldestFirst);

        // Oldest first still starts at f0001, and it should be the same tile, not a new one.
        Assert.Same(kept, m.Incoming.First(f => f.FileName == "f0001.png"));
    }

    [Fact]
    public void The_status_line_says_how_much_of_the_folder_is_showing()
    {
        var (m, temp) = Open(fileCount: 50, pageSize: 10);
        using var _t = temp;

        Assert.Contains("showing 10 of 50", m.StatusMessage);
    }
}

/// <summary>
/// Thumbnails. A blank tile looks the same whether decoding failed or the file has no preview,
/// so these check that every visible tile was at least asked to decode.
/// </summary>
public sealed class KibbleThumbnailTests
{
    private static (KibbleViewModel Model, TempWorkspace Temp) Open(int count, int pageSize)
    {
        var temp = new TempWorkspace();
        temp.WriteConfig(temp.AddGroup("Alpha"));
        var folder = Path.Combine(temp.Root, "intake");
        Directory.CreateDirectory(folder);
        for (var i = 1; i <= count; i++)
            File.WriteAllBytes(Path.Combine(folder, $"f{i:D4}.png"), System.Text.Encoding.UTF8.GetBytes($"f{i}"));

        var model = new KibbleViewModel(new FakeHost(Path.Combine(temp.Root, "hostdata")));
        model.SetBotRoot(temp.Workspace.Root);
        model.PageSize = pageSize;
        model.LazyLoad = true;
        model.LoadFolder(folder);
        return (model, temp);
    }

    [Fact]
    public async Task Every_tile_in_the_first_batch_is_asked()
    {
        var (m, temp) = Open(count: 30, pageSize: 10);
        using var _t = temp;

        await m.ThumbnailPass;

        Assert.All(m.Incoming, f => Assert.True(f.ThumbnailAttempted));
    }

    [Fact]
    public async Task Tiles_that_slide_in_after_a_send_are_asked_too()
    {
        var (m, temp) = Open(count: 30, pageSize: 10);
        using var _t = temp;
        await m.ThumbnailPass;

        m.SetSelection([m.Incoming[0]]);
        m.SendToCommand.Execute(m.Destinations[0]);
        await m.ThumbnailPass;

        // The replacement tile arrives brand new. Before this was fixed it stayed blank for
        // good, so working through a folder left more and more empty squares behind.
        Assert.Equal(10, m.Incoming.Count);
        Assert.All(m.Incoming, f => Assert.True(f.ThumbnailAttempted));
    }

    [Fact]
    public async Task A_whole_batch_leaving_still_leaves_every_replacement_asked()
    {
        var (m, temp) = Open(count: 30, pageSize: 10);
        using var _t = temp;
        await m.ThumbnailPass;

        m.SetSelection([.. m.Incoming]);
        m.SendToCommand.Execute(m.Destinations[0]);
        await m.ThumbnailPass;

        Assert.Equal(10, m.Incoming.Count);
        Assert.All(m.Incoming, f => Assert.True(f.ThumbnailAttempted));
    }

    [Fact]
    public async Task Load_more_asks_the_new_batch()
    {
        var (m, temp) = Open(count: 30, pageSize: 10);
        using var _t = temp;
        await m.ThumbnailPass;

        m.LoadMoreCommand.Execute(null);
        await m.ThumbnailPass;

        Assert.Equal(20, m.Incoming.Count);
        Assert.All(m.Incoming, f => Assert.True(f.ThumbnailAttempted));
    }

    [Fact]
    public async Task Sorting_leaves_everything_on_screen_asked()
    {
        var (m, temp) = Open(count: 30, pageSize: 10);
        using var _t = temp;
        await m.ThumbnailPass;

        m.SelectedSort = m.SortOptions.First(o => o.Value == GridSort.NewestFirst);
        await m.ThumbnailPass;

        Assert.All(m.Incoming, f => Assert.True(f.ThumbnailAttempted));
    }

    [Fact]
    public async Task A_cancelled_decode_does_not_count_as_having_tried()
    {
        using var temp = new TempWorkspace();
        var path = Path.Combine(temp.Root, "a.png");
        File.WriteAllBytes(path, [1, 2, 3]);
        var file = new IncomingFileViewModel(path, 3, DateTime.UtcNow);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => file.LoadThumbnailAsync(150, cts.Token));

        // Otherwise one cancelled pass, from a sort or a Load more, blanks a tile permanently.
        Assert.False(file.ThumbnailAttempted);
    }
}

/// <summary>Naming a comic from the files that go into it.</summary>
public sealed class ComicNameTests
{
    [Fact]
    public void The_commonest_shared_words_become_the_name()
    {
        var name = ComicName.Weighted(["foxy_cafe_01.png", "foxy_cafe_02.png", "foxy_diner_03.png"], "fallback");

        // foxy is in all three, cafe in two, diner in only one.
        Assert.Equal("foxy cafe", name);
    }

    [Fact]
    public void Page_numbers_are_what_differs_so_they_are_dropped()
    {
        var name = ComicName.Weighted(["set_01.png", "set_02.png", "set_03.png"], "fallback");

        Assert.Equal("set", name);
    }

    [Fact]
    public void Hash_named_downloads_fall_back_to_the_folder()
    {
        var name = ComicName.Weighted(
            ["5d54bd8760307d78940e1f38f144004e.png", "9af12cc4410207e881a0f27b6633115f.png"],
            "furry paws");

        // Nothing is shared, so inventing something from the hashes would be worse than useless.
        Assert.Equal("furry paws", name);
    }

    [Fact]
    public void A_word_repeated_inside_one_file_does_not_outvote_the_others()
    {
        var name = ComicName.Weighted(["loud_loud_loud_alpha.png", "quiet_alpha.png"], "fallback");

        // alpha is in both files, loud only in one, however many times it says it.
        Assert.Equal("alpha", name);
    }

    [Fact]
    public void The_shared_words_keep_the_order_they_read_in()
    {
        var name = ComicName.Weighted(["red_barn_one.png", "red_barn_two.png"], "fallback");

        Assert.Equal("red barn", name);
    }

    [Fact]
    public void A_single_file_still_gives_its_own_words()
    {
        var name = ComicName.Weighted(["lonely_cabin.png"], "fallback");

        Assert.Equal("lonely cabin", name);
    }

    [Fact]
    public void Nothing_picked_falls_back()
    {
        Assert.Equal("fallback", ComicName.Weighted([], "fallback"));
    }

    [Fact]
    public void A_random_tag_hangs_off_the_folder_name()
    {
        var name = ComicName.WithRandomTag("bigfolder");

        Assert.StartsWith("bigfolder-", name);
        Assert.Equal("bigfolder".Length + 5, name.Length);
    }

    [Fact]
    public void Two_random_tags_are_not_the_same()
    {
        var names = Enumerable.Range(0, 40).Select(_ => ComicName.WithRandomTag("set")).ToHashSet();

        // The whole point is that picks never collide, so 40 draws should not all agree.
        Assert.True(names.Count > 30, $"only {names.Count} distinct out of 40");
    }

    [Fact]
    public void Characters_a_file_name_cannot_hold_never_survive()
    {
        var weighted = ComicName.Weighted([], "bad/name:here");
        var random = ComicName.WithRandomTag("bad/name:here");

        Assert.DoesNotContain('/', weighted);
        Assert.DoesNotContain(':', weighted);
        Assert.DoesNotContain('/', random);
    }
}

public sealed class KibbleNamingTests
{
    private static (KibbleViewModel Model, TempWorkspace Temp) Open(params string[] names)
    {
        var temp = new TempWorkspace();
        temp.WriteConfig(temp.AddGroup("Alpha"));
        var folder = Path.Combine(temp.Root, "bigfolder");
        Directory.CreateDirectory(folder);
        foreach (var n in names)
            File.WriteAllBytes(Path.Combine(folder, n), System.Text.Encoding.UTF8.GetBytes(n));

        var model = new KibbleViewModel(new FakeHost(Path.Combine(temp.Root, "hostdata")));
        model.SetBotRoot(temp.Workspace.Root);
        model.LoadFolder(folder);
        return (model, temp);
    }

    private static void Naming(KibbleViewModel m, ComicNaming rule) =>
        m.SelectedNaming = m.NamingOptions.First(o => o.Value == rule);

    [Fact]
    public void The_folder_rule_leaves_the_name_alone()
    {
        var (m, temp) = Open("foxy_cafe_01.png", "foxy_cafe_02.png");
        using var _t = temp;

        m.SetSelection([.. m.Incoming]);

        Assert.Equal("bigfolder", m.ArchiveName);
    }

    [Fact]
    public void Weighted_names_the_comic_after_the_pick()
    {
        var (m, temp) = Open("foxy_cafe_01.png", "foxy_cafe_02.png", "foxy_diner_03.png");
        using var _t = temp;
        Naming(m, ComicNaming.Weighted);

        m.SetSelection([.. m.Incoming]);

        Assert.Equal("foxy cafe", m.ArchiveName);
    }

    [Fact]
    public void Weighted_follows_the_pick_as_it_changes()
    {
        var (m, temp) = Open("red_barn_one.png", "red_barn_two.png", "blue_lake_three.png");
        using var _t = temp;
        Naming(m, ComicNaming.Weighted);
        var barn = m.Incoming.Where(f => f.FileName.StartsWith("red")).ToList();

        m.SetSelection(barn);
        Assert.Equal("red barn", m.ArchiveName);

        // A pick where no word appears in two files has nothing to go on, so it falls back.
        var mixed = new[] { m.Incoming[0], m.Incoming.First(f => f.FileName.StartsWith("blue")) };
        m.SetSelection(mixed);
        Assert.Equal("bigfolder", m.ArchiveName);
    }

    [Fact]
    public void A_random_tag_holds_still_while_you_add_to_the_pick()
    {
        var (m, temp) = Open("a.png", "b.png", "c.png");
        using var _t = temp;
        Naming(m, ComicNaming.RandomTag);

        m.SetSelection([m.Incoming[0], m.Incoming[1]]);
        var first = m.ArchiveName;
        m.SetSelection([.. m.Incoming]);

        Assert.StartsWith("bigfolder-", first);
        Assert.Equal(first, m.ArchiveName);
    }

    [Fact]
    public void A_fresh_pick_gets_a_fresh_tag()
    {
        var (m, temp) = Open("a.png", "b.png", "c.png", "d.png");
        using var _t = temp;
        Naming(m, ComicNaming.RandomTag);

        m.SetSelection([m.Incoming[0], m.Incoming[1]]);
        var first = m.ArchiveName;
        m.SetSelection([]);
        m.SetSelection([m.Incoming[2], m.Incoming[3]]);

        Assert.NotEqual(first, m.ArchiveName);
        Assert.StartsWith("bigfolder-", m.ArchiveName);
    }

    [Fact]
    public void The_suggested_name_is_what_the_archive_is_actually_called()
    {
        var (m, temp) = Open("foxy_cafe_01.png", "foxy_cafe_02.png");
        using var _t = temp;
        Naming(m, ComicNaming.Weighted);

        m.SetSelection([.. m.Incoming]);
        var expected = m.ArchiveName;
        m.SendToCommand.Execute(m.Destinations[0]);

        var queued = temp.Workspace.Scan(temp.Workspace.ToSendFolder(m.Destinations[0].Group));
        Assert.Equal($"{expected}.cbz", Path.GetFileName(queued[0]));
    }

    [Fact]
    public void You_can_still_type_over_the_suggestion()
    {
        var (m, temp) = Open("foxy_cafe_01.png", "foxy_cafe_02.png");
        using var _t = temp;
        Naming(m, ComicNaming.Weighted);
        m.SetSelection([.. m.Incoming]);

        m.ArchiveName = "my own name";
        m.SendToCommand.Execute(m.Destinations[0]);

        var queued = temp.Workspace.Scan(temp.Workspace.ToSendFolder(m.Destinations[0].Group));
        Assert.Equal("my own name.cbz", Path.GetFileName(queued[0]));
    }

    [Fact]
    public void Switching_rule_updates_the_box_without_waiting_for_a_click()
    {
        var (m, temp) = Open("foxy_cafe_01.png", "foxy_cafe_02.png");
        using var _t = temp;
        m.SetSelection([.. m.Incoming]);
        Assert.Equal("bigfolder", m.ArchiveName);

        Naming(m, ComicNaming.Weighted);

        Assert.Equal("foxy cafe", m.ArchiveName);
    }
}
