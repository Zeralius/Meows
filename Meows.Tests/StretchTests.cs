using System.Text.Json;
using Meows.Bot;
using Meows.Plugins.TelegramPoster.ViewModels;

namespace Meows.Tests;

/// <summary>
/// Slowing a group down so a short queue lasts longer.
///
/// The stretching itself happens in bot.py, because only the bot knows the queue at the moment
/// it posts and only it can retime a job that is already running. What is checked here is the
/// copy of the sum that Meows uses to show what the bot is going to do, and the two have to
/// agree or the number on screen is a different number from the one being used.
/// </summary>
public class StretchTests
{
    private static GroupConfig Group(int intervalMinutes = 60, int filesPerPost = 1,
        double? targetDays = 7, int? cap = 720) =>
        new()
        {
            Name = "Test",
            ChatId = "-1001234567890",
            Folder = "groups/test",
            Schedule = new ScheduleConfig { IntervalMinutes = intervalMinutes },
            FilesPerPost = filesPerPost,
            Stretch = targetDays is null ? null : new StretchConfig
            {
                TargetDays = targetDays,
                MaxIntervalMinutes = cap,
            },
        };

    /// <summary>
    /// The same table that was run against bot.py's stretched_interval, at the settings the
    /// real groups use: hourly, one file a post, aiming at a week, never slower than twelve
    /// hours. If these two ever disagree, the plugin is telling you something the bot is not
    /// doing.
    /// </summary>
    [Theory]
    [InlineData(0, 60)]      // empty, so there is nothing to spread out
    [InlineData(1, 720)]     // wants 7 days between posts, gets the cap
    [InlineData(3, 720)]
    [InlineData(5, 720)]
    [InlineData(14, 720)]    // exactly the cap, and exactly the target
    [InlineData(56, 180)]    // 3 hours, reaching the target without the cap
    [InlineData(160, 63)]    // barely slowed
    [InlineData(180, 60)]    // already past the target, so left alone
    [InlineData(785, 60)]
    [InlineData(1214, 60)]
    public void The_interval_matches_what_the_bot_would_choose(int queued, int expected)
    {
        Assert.Equal(expected, QueueRunway.StretchedIntervalMinutes(Group(), queued));
    }

    [Fact]
    public void A_healthy_queue_is_never_sped_up()
    {
        // Stretching only ever slows a group down. The interval in config.json stays the
        // fastest it will ever post, which is what makes it safe to leave switched on.
        Assert.Equal(60, QueueRunway.StretchedIntervalMinutes(Group(), 100_000));
    }

    [Fact]
    public void An_empty_queue_is_left_at_its_own_pace()
    {
        // There is nothing left to make last. Slowing down here would only space out the
        // archive repeats, which helps nobody.
        Assert.Equal(60, QueueRunway.StretchedIntervalMinutes(Group(), 0));
        Assert.False(QueueRunway.IsStretching(Group(), 0));
    }

    [Fact]
    public void A_group_with_no_stretch_is_not_stretched()
    {
        Assert.Null(QueueRunway.StretchedIntervalMinutes(Group(targetDays: null), 5));
        Assert.False(QueueRunway.IsStretching(Group(targetDays: null), 5));
    }

    [Fact]
    public void A_daily_group_is_not_stretched()
    {
        // No interval to widen. bot.py warns and ignores it, so this has to agree.
        var daily = Group();
        daily.Schedule = new ScheduleConfig { Hour = 12, Minute = 0 };

        Assert.Null(QueueRunway.StretchedIntervalMinutes(daily, 5));
    }

    [Fact]
    public void A_missing_cap_falls_back_to_the_same_default_as_the_bot()
    {
        var group = Group(cap: null);

        // A day, matching DEFAULT_STRETCH_CAP_MINUTES in bot.py.
        Assert.Equal(1440, QueueRunway.StretchedIntervalMinutes(group, 1));
    }

    [Fact]
    public void Posts_are_counted_rather_than_files()
    {
        // Nine files at three a post is three posts, which should stretch exactly as far as
        // three files at one a post.
        Assert.Equal(
            QueueRunway.StretchedIntervalMinutes(Group(filesPerPost: 1, cap: 100_000), 3),
            QueueRunway.StretchedIntervalMinutes(Group(filesPerPost: 3, cap: 100_000), 9));
    }

    [Fact]
    public void The_runway_shown_is_the_stretched_one()
    {
        // Five files at one an hour is five hours. Stretched to twelve hours apart it is two
        // and a half days, and that is the honest answer to "when does this run out".
        Assert.Equal(0.21, QueueRunway.Days(Group(targetDays: null), 5)!.Value, 2);
        Assert.Equal(2.5, QueueRunway.Days(Group(), 5)!.Value, 2);
    }

    [Fact]
    public void A_disabled_group_has_no_runway_even_when_stretched()
    {
        var group = Group();
        group.Enabled = false;

        Assert.Null(QueueRunway.Days(group, 5));
    }
}

/// <summary>
/// config.json survives a trip through Meows.
///
/// Saving rewrites the whole file from these objects, so anything the model does not carry is
/// something saving deletes. start_offset_minutes was exactly that: eleven groups spaced five
/// minutes apart so they do not all post on the hour, and one press of Save would have flattened
/// every one of them to zero.
/// </summary>
public class BotConfigTests
{
    private const string Written = """
        {
          "groups": [
            {
              "name": "Furry Armpit",
              "chat_id": "-1001234567890",
              "folder": "groups/furry_armpit",
              "schedule": { "interval_minutes": 60 },
              "jitter_minutes": 2,
              "start_offset_minutes": 5,
              "files_per_post": 1,
              "post_order": "oldest",
              "comic_order": "name",
              "stretch": { "target_days": 7, "max_interval_minutes": 720 },
              "something_the_bot_gains_later": { "nested": [1, 2, 3] }
            }
          ]
        }
        """;

    private static GroupConfig Read() =>
        JsonSerializer.Deserialize<BotConfig>(Written, BotConfig.JsonOptions)!.Groups.Single();

    [Fact]
    public void The_spacing_between_groups_is_read()
    {
        Assert.Equal(5, Read().StartOffsetMinutes);
    }

    [Fact]
    public void The_stretch_is_read()
    {
        var stretch = Read().Stretch;

        Assert.NotNull(stretch);
        Assert.Equal(7, stretch.TargetDays);
        Assert.Equal(720, stretch.MaxIntervalMinutes);
    }

    [Fact]
    public void Saving_keeps_everything_it_was_given()
    {
        var back = JsonSerializer.Serialize(
            new BotConfig { Groups = [Read()] }, BotConfig.JsonOptions);

        using var reread = JsonDocument.Parse(back);
        var group = reread.RootElement.GetProperty("groups")[0];

        Assert.Equal(5, group.GetProperty("start_offset_minutes").GetInt32());
        Assert.Equal(2, group.GetProperty("jitter_minutes").GetInt32());
        Assert.Equal(7, group.GetProperty("stretch").GetProperty("target_days").GetDouble());
        Assert.Equal(60, group.GetProperty("schedule").GetProperty("interval_minutes").GetInt32());
    }

    [Fact]
    public void A_key_this_has_never_heard_of_survives_a_save()
    {
        // The bot is a separate program on its own schedule. It will gain keys this does not
        // know about, and losing them silently is the worst way to find out.
        var back = JsonSerializer.Serialize(
            new BotConfig { Groups = [Read()] }, BotConfig.JsonOptions);

        using var reread = JsonDocument.Parse(back);
        var kept = reread.RootElement.GetProperty("groups")[0]
            .GetProperty("something_the_bot_gains_later").GetProperty("nested");

        Assert.Equal([1, 2, 3], kept.EnumerateArray().Select(e => e.GetInt32()));
    }

    [Fact]
    public void Cloning_carries_everything_too()
    {
        // The editor works on a clone and writes it back, so a clone that forgets a key loses
        // it just as thoroughly as a model that never had one.
        var clone = Read().Clone();

        Assert.Equal(5, clone.StartOffsetMinutes);
        Assert.Equal(7, clone.Stretch!.TargetDays);
        Assert.True(clone.Extra!.ContainsKey("something_the_bot_gains_later"));
    }

    [Fact]
    public void A_clone_is_not_the_same_object()
    {
        var original = Read();
        var clone = original.Clone();
        clone.Stretch!.TargetDays = 99;
        clone.Extra!.Clear();

        Assert.Equal(7, original.Stretch!.TargetDays);
        Assert.NotEmpty(original.Extra!);
    }
}

/// <summary>
/// Editing a group in the plugin and saving it.
///
/// The model surviving a round trip was not enough on its own: the editor does not save what it
/// read, it builds a fresh group out of the fields on screen. Anything the editor has no field
/// for has to be carried across deliberately, or Save quietly drops it.
/// </summary>
public class GroupEditingTests : IDisposable
{
    private readonly TempWorkspace _bot = new();

    public void Dispose() => _bot.Dispose();

    private GroupViewModel Editing(GroupConfig config) =>
        new(config, _bot.Workspace, () => { });

    private GroupConfig Configured()
    {
        var group = _bot.AddGroup("Furry Armpit");
        group.Schedule = new ScheduleConfig { IntervalMinutes = 60 };
        group.JitterMinutes = 2;
        group.StartOffsetMinutes = 5;
        group.FilesPerPost = 1;
        group.Extra = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """{ "something_the_bot_gains_later": 42 }""");
        return group;
    }

    [Fact]
    public void Editing_one_field_does_not_drop_the_others()
    {
        var editing = Editing(Configured());

        // Change something the editor does own.
        editing.JitterMinutes = 9;
        var saved = editing.ToConfig();

        Assert.Equal(9, saved.JitterMinutes);

        // Eleven groups are spaced five minutes apart so they do not all post on the hour.
        // Saving used to flatten every one of them.
        Assert.Equal(5, saved.StartOffsetMinutes);
        Assert.Equal(42, saved.Extra!["something_the_bot_gains_later"].GetInt32());
    }

    [Fact]
    public void A_stretch_is_written_when_it_is_switched_on()
    {
        var editing = Editing(Configured());

        editing.StretchEnabled = true;
        editing.StretchTargetDays = 7;
        editing.StretchCapMinutes = 720;

        var saved = editing.ToConfig();
        Assert.Equal(7, saved.Stretch!.TargetDays);
        Assert.Equal(720, saved.Stretch.MaxIntervalMinutes);
    }

    [Fact]
    public void Switching_it_off_takes_it_out_of_the_file_rather_than_writing_a_zero()
    {
        var group = Configured();
        group.Stretch = new StretchConfig { TargetDays = 7, MaxIntervalMinutes = 720 };

        var editing = Editing(group);
        Assert.True(editing.StretchEnabled);

        editing.StretchEnabled = false;

        // bot.py treats a missing block as off. A block with a zero in it would be a warning
        // in the log every startup instead.
        Assert.Null(editing.ToConfig().Stretch);
    }

    [Fact]
    public void Changing_the_stretch_marks_the_group_unsaved()
    {
        var editing = Editing(Configured());
        Assert.False(editing.IsDirty);

        editing.StretchEnabled = true;

        Assert.True(editing.IsDirty);
    }

    [Fact]
    public void Reverting_puts_the_stretch_back()
    {
        var group = Configured();
        group.Stretch = new StretchConfig { TargetDays = 3, MaxIntervalMinutes = 240 };

        var editing = Editing(group);
        editing.StretchTargetDays = 30;
        editing.Revert();

        Assert.Equal(3, editing.StretchTargetDays);
        Assert.Equal(240, editing.StretchCapMinutes);
        Assert.False(editing.IsDirty);
    }
}
