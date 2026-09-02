using Meows.Plugins.TelegramPoster.Services;
using Meows.Bot;

namespace Meows.Tests;

public sealed class BotSetupTests
{
    [Fact]
    public void Writing_a_token_creates_env_when_there_is_none()
    {
        using var temp = new TempWorkspace();

        BotSetup.WriteToken(temp.Root, "123456:abc");

        Assert.True(new BotWorkspace(temp.Root).HasToken);
        Assert.Equal("BOT_TOKEN=123456:abc", File.ReadAllLines(temp.Workspace.EnvPath).Single());
    }

    [Fact]
    public void Writing_a_token_replaces_in_place_and_keeps_other_lines()
    {
        using var temp = new TempWorkspace();
        File.WriteAllLines(temp.Workspace.EnvPath, ["OTHER=keep", "BOT_TOKEN=old", "TRAILING=keep"]);

        BotSetup.WriteToken(temp.Root, "new-value");
        var lines = File.ReadAllLines(temp.Workspace.EnvPath);

        Assert.Single(lines, l => l.StartsWith("BOT_TOKEN=", StringComparison.Ordinal));
        Assert.Contains("BOT_TOKEN=new-value", lines);
        Assert.Contains("OTHER=keep", lines);
        Assert.Contains("TRAILING=keep", lines);
    }

    [Fact]
    public void An_empty_destination_is_rejected()
    {
        Assert.NotNull(BotSetup.DestinationProblem(""));
    }

    [Fact]
    public void A_non_empty_destination_is_rejected_before_git_is_run()
    {
        using var temp = new TempWorkspace();

        // git clone would fail on this anyway; saying so first is clearer than its error.
        Assert.NotNull(BotSetup.DestinationProblem(temp.Root));
    }

    [Fact]
    public void A_fresh_destination_is_accepted()
    {
        using var temp = new TempWorkspace();

        Assert.Null(BotSetup.DestinationProblem(Path.Combine(temp.Root, "does-not-exist-yet")));
    }

    [Fact]
    public void The_default_clone_url_points_at_the_public_bot()
    {
        Assert.StartsWith("https://", BotSetup.DefaultRepositoryUrl);
        Assert.EndsWith("telegram-posting-bot.git", BotSetup.DefaultRepositoryUrl);
    }
}

public sealed class ToolProbeTests
{
    [Fact]
    public async Task A_command_that_does_not_exist_is_reported_missing()
    {
        var result = await ToolProbe.FindPythonAsync("definitely-not-a-real-interpreter-xyz");

        // It falls through to the usual candidates, so this only asserts it never claims the
        // bogus name works.
        Assert.NotEqual("definitely-not-a-real-interpreter-xyz", result.Command);
    }

    [Fact]
    public async Task Git_is_detected_on_a_machine_that_has_it()
    {
        var git = await ToolProbe.FindGitAsync();

        // CI runners and dev machines both have git; if this ever fails the environment is
        // the story, not the code.
        Assert.True(git.Found);
        Assert.Contains("git version", git.Version);
    }
}

public sealed class MediaRulesTests
{
    [Theory]
    [InlineData("page2.png", "page10.png")]
    [InlineData("a1.jpg", "a10.jpg")]
    [InlineData("img_9.png", "img_100.png")]
    public void Digit_runs_compare_numerically(string smaller, string larger)
    {
        Assert.True(MediaRules.CompareNatural(smaller, larger) < 0);
    }

    [Theory]
    [InlineData("a.png", MediaKind.Photo)]
    [InlineData("a.JPG", MediaKind.Photo)]
    [InlineData("a.webp", MediaKind.Photo)]
    [InlineData("a.mp4", MediaKind.Video)]
    [InlineData("a.gif", MediaKind.Animation)]
    [InlineData("a.pdf", MediaKind.Document)]
    [InlineData("a.zip", MediaKind.Comic)]
    [InlineData("a.cbz", MediaKind.Comic)]
    [InlineData("a.txt", MediaKind.Unsupported)]
    public void Extensions_map_to_the_kinds_the_bot_uses(string name, MediaKind expected)
    {
        Assert.Equal(expected, MediaRules.KindOf(name));
    }

    [Fact]
    public void Only_postable_extensions_count()
    {
        Assert.True(MediaRules.IsPostable("x.png"));
        Assert.False(MediaRules.IsPostable("x.nfo"));
    }
}
