using Meows.Bot;

namespace Meows.Tests;

/// <summary>One answer to "where is the bot", shared by every plugin that asks.</summary>
public class BotLocationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "meows-botloc-" + Guid.NewGuid().ToString("N"));
    private readonly string _previousFolder = BotLocation.SettingsFolder;

    public BotLocationTests()
    {
        Directory.CreateDirectory(_root);
        BotLocation.SettingsFolder = Path.Combine(_root, "settings");
    }

    public void Dispose()
    {
        BotLocation.SettingsFolder = _previousFolder;
        Directory.Delete(_root, recursive: true);
    }

    private string Bot(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "bot.py"), "#");
        File.WriteAllText(Path.Combine(path, "config.json"), "{}");
        return path;
    }

    [Fact]
    public void Nothing_is_shared_until_somebody_picks()
    {
        Assert.Null(BotLocation.Shared());
    }

    [Fact]
    public void A_pick_in_one_plugin_is_what_a_plugin_with_no_opinion_reads()
    {
        var bot = Bot("bot");

        BotLocation.Remember(bot);

        Assert.Equal(bot, BotLocation.Shared());
        Assert.Equal(bot, BotLocation.Resolve(null));
    }

    [Fact]
    public void A_root_a_plugin_was_given_by_hand_still_wins()
    {
        var shared = Bot("shared");
        var mine = Bot("mine");
        BotLocation.Remember(shared);

        Assert.Equal(mine, BotLocation.Resolve(mine));
    }

    [Fact]
    public void A_shared_folder_that_has_gone_is_not_an_answer()
    {
        var bot = Bot("gone");
        BotLocation.Remember(bot);
        Directory.Delete(bot, recursive: true);

        Assert.Null(BotLocation.Shared());
    }
}
