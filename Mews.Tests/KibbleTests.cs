using Mews.Bot;
using Mews.Plugins.Kibble.Services;

namespace Mews.Tests;

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
