using Mews.Plugins.TelegramPoster.Model;
using Mews.Plugins.TelegramPoster.Services;
using Mews.Bot;

namespace Mews.Tests;

public sealed class BotWorkspaceTests
{
    [Fact]
    public void Saving_preserves_every_field_of_every_group()
    {
        using var temp = new TempWorkspace();
        var config = new BotConfig
        {
            Groups =
            [
                new GroupConfig
                {
                    Name = "Alpha", ChatId = "-100111", Folder = "groups/alpha",
                    Schedule = new ScheduleConfig { Hour = 9, Minute = 30 },
                    JitterMinutes = 5, FilesPerPost = 2, PostOrder = "random", ComicOrder = "date",
                },
            ],
        };

        temp.Workspace.SaveConfig(config);
        var reread = temp.Workspace.LoadConfig().Groups.Single();

        Assert.Equal("Alpha", reread.Name);
        Assert.Equal("-100111", reread.ChatId);
        Assert.Equal(9, reread.Schedule.Hour);
        Assert.Equal(30, reread.Schedule.Minute);
        Assert.Equal(5, reread.JitterMinutes);
        Assert.Equal(2, reread.FilesPerPost);
        Assert.Equal("random", reread.PostOrder);
        Assert.Equal("date", reread.ComicOrder);
    }

    [Fact]
    public void Enabled_is_written_only_when_a_group_is_disabled()
    {
        using var temp = new TempWorkspace();
        temp.Workspace.SaveConfig(new BotConfig
        {
            Groups =
            [
                new GroupConfig { Name = "On", ChatId = "-1", Folder = "groups/on", Enabled = null },
                new GroupConfig { Name = "Off", ChatId = "-2", Folder = "groups/off", Enabled = false },
            ],
        });

        var json = File.ReadAllText(temp.Workspace.ConfigPath);
        var onBlock = json[json.IndexOf("\"On\"", StringComparison.Ordinal)..json.IndexOf("\"Off\"", StringComparison.Ordinal)];

        // Absent already means enabled to bot.py, so writing "true" would only add noise.
        Assert.DoesNotContain("enabled", onBlock, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"enabled\": false", json);
    }

    [Fact]
    public void Group_folders_compare_equal_across_separator_styles()
    {
        using var temp = new TempWorkspace();
        var forward = new GroupConfig { Folder = "groups/x" };
        var back = new GroupConfig { Folder = @"groups\x" };

        Assert.Equal(temp.Workspace.GroupFolder(forward), temp.Workspace.GroupFolder(back));
    }

    [Fact]
    public void Next_up_picks_the_oldest_by_modified_time()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");
        group.PostOrder = "oldest";
        temp.Queue(group, "new.png", new DateTime(2025, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        temp.Queue(group, "old.png", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var next = temp.Workspace.ResolveNextUp(group);

        Assert.Equal(NextUpKind.Known, next.Kind);
        Assert.Equal("old.png", Path.GetFileName(next.Files.Single()));
    }

    [Fact]
    public void Next_up_reverses_for_newest()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");
        group.PostOrder = "newest";
        temp.Queue(group, "new.png", new DateTime(2025, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        temp.Queue(group, "old.png", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal("new.png", Path.GetFileName(temp.Workspace.ResolveNextUp(group).Files.Single()));
    }

    [Fact]
    public void Random_order_reports_that_there_is_nothing_to_preview()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");
        group.PostOrder = "random";
        temp.Queue(group, "a.png", DateTime.UtcNow);

        var next = temp.Workspace.ResolveNextUp(group);

        // The bot draws at post time, so claiming to know the file would be a lie.
        Assert.Equal(NextUpKind.RandomAtPostTime, next.Kind);
        Assert.Empty(next.Files);
    }

    [Fact]
    public void An_empty_queue_falls_back_to_the_archive()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");
        File.WriteAllBytes(Path.Combine(temp.Workspace.AlreadySentFolder(group), "sent.png"), [1]);

        Assert.Equal(NextUpKind.FallbackRandom, temp.Workspace.ResolveNextUp(group).Kind);
    }

    [Fact]
    public void Nothing_anywhere_is_its_own_state()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");

        Assert.Equal(NextUpKind.Nothing, temp.Workspace.ResolveNextUp(group).Kind);
    }

    [Fact]
    public void Files_per_post_takes_that_many()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");
        group.FilesPerPost = 2;
        temp.Queue(group, "a.png", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        temp.Queue(group, "b.png", new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        temp.Queue(group, "c.png", new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(2, temp.Workspace.ResolveNextUp(group).Files.Count);
    }

    [Fact]
    public void A_checkout_is_only_valid_with_both_bot_and_config()
    {
        using var temp = new TempWorkspace();
        Assert.True(temp.Workspace.LooksValid);

        File.Delete(temp.Workspace.ConfigPath);
        Assert.False(new BotWorkspace(temp.Root).LooksValid);
    }

    [Fact]
    public void Token_presence_is_read_from_env_without_exposing_it()
    {
        using var temp = new TempWorkspace();
        Assert.False(temp.Workspace.HasToken);

        File.WriteAllText(temp.Workspace.EnvPath, "BOT_TOKEN=\n");
        Assert.False(new BotWorkspace(temp.Root).HasToken);

        File.WriteAllText(temp.Workspace.EnvPath, "BOT_TOKEN=123456:abc\n");
        Assert.True(new BotWorkspace(temp.Root).HasToken);
    }
}
