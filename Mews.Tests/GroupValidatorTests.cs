using Mews.Plugins.TelegramPoster.Model;
using Mews.Plugins.TelegramPoster.Services;

namespace Mews.Tests;

public sealed class GroupValidatorTests
{
    private static IssueSeverity? Worst(IReadOnlyList<GroupIssue> issues) =>
        issues.Count == 0 ? null
        : issues.Any(i => i.IsError) ? IssueSeverity.Error
        : IssueSeverity.Warning;

    [Fact]
    public void A_fully_configured_group_reports_nothing()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("Good");
        temp.Queue(group, "a.png", DateTime.UtcNow);

        var issues = GroupValidator.Validate(group, temp.Workspace, [], 1, 0);

        Assert.Empty(issues);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_chat_id_is_an_error(string chatId)
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G", chatId);

        Assert.Equal(IssueSeverity.Error, Worst(GroupValidator.Validate(group, temp.Workspace, [], 1, 0)));
    }

    [Theory]
    [InlineData("REPLACE_WITH_CHAT_ID")]
    [InlineData("replace_with_chat_id")]
    [InlineData("YOUR_CHAT_ID")]
    [InlineData("-1001234567890")]
    [InlineData("-1009876543210")]
    public void An_unfilled_placeholder_is_an_error(string chatId)
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G", chatId);

        var issues = GroupValidator.Validate(group, temp.Workspace, [], 1, 0);

        Assert.Equal(IssueSeverity.Error, Worst(issues));
        Assert.Contains(issues, i => i.Message.Contains("placeholder", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Two_untouched_placeholders_do_not_also_report_a_duplicate()
    {
        using var temp = new TempWorkspace();
        var a = temp.AddGroup("A", "REPLACE_WITH_CHAT_ID");
        var b = temp.AddGroup("B", "REPLACE_WITH_CHAT_ID");
        temp.Queue(a, "x.png", DateTime.UtcNow);

        var issues = GroupValidator.Validate(a, temp.Workspace, [b], 1, 0);

        // The placeholder is the real problem. Reporting a clash between two of them only
        // distracts, so exactly one issue should come back.
        Assert.Single(issues);
    }

    [Fact]
    public void A_public_channel_name_is_accepted()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G", "@mychannel");
        temp.Queue(group, "a.png", DateTime.UtcNow);

        Assert.Empty(GroupValidator.Validate(group, temp.Workspace, [], 1, 0));
    }

    [Theory]
    [InlineData("12345", "private chat")]
    [InlineData("not a chat id", "neither a number")]
    public void A_malformed_chat_id_warns_without_blocking(string chatId, string expected)
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G", chatId);
        temp.Queue(group, "a.png", DateTime.UtcNow);

        var issues = GroupValidator.Validate(group, temp.Workspace, [], 1, 0);

        Assert.Equal(IssueSeverity.Warning, Worst(issues));
        Assert.Contains(issues, i => i.Message.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_blank_name_or_folder_is_an_error()
    {
        using var temp = new TempWorkspace();
        var noName = temp.AddGroup("G");
        noName.Name = "";
        var noFolder = temp.AddGroup("H");
        noFolder.Folder = "";

        Assert.Equal(IssueSeverity.Error, Worst(GroupValidator.Validate(noName, temp.Workspace, [], 1, 0)));
        Assert.Equal(IssueSeverity.Error, Worst(GroupValidator.Validate(noFolder, temp.Workspace, [], 1, 0)));
    }

    [Fact]
    public void A_duplicate_chat_id_explains_that_only_the_last_group_is_scheduled()
    {
        using var temp = new TempWorkspace();
        var a = temp.AddGroup("A");
        var b = temp.AddGroup("B");
        temp.Queue(a, "x.png", DateTime.UtcNow);

        var issues = GroupValidator.Validate(a, temp.Workspace, [b], 1, 0);

        Assert.Contains(issues, i => i.Message.Contains("only the last of them is scheduled"));
    }

    [Fact]
    public void A_shared_folder_is_detected_across_separator_styles()
    {
        using var temp = new TempWorkspace();
        var a = temp.AddGroup("A", folder: "groups/shared");
        var b = temp.AddGroup("B", "-100999", folder: @"groups\shared");
        temp.Queue(a, "x.png", DateTime.UtcNow);

        var issues = GroupValidator.Validate(a, temp.Workspace, [b], 1, 0);

        Assert.Contains(issues, i => i.Message.Contains("Shares its folder"));
    }

    [Fact]
    public void A_group_with_nothing_anywhere_is_reported()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("Empty");

        var issues = GroupValidator.Validate(group, temp.Workspace, [], 0, 0);

        Assert.Contains(issues, i => i.Message.Contains("nothing to post"));
    }

    [Fact]
    public void A_folder_that_does_not_exist_warns_rather_than_blocks()
    {
        using var temp = new TempWorkspace();
        var group = temp.AddGroup("G");
        group.Folder = "groups/never_created";

        var issues = GroupValidator.Validate(group, temp.Workspace, [], 1, 0);

        Assert.Equal(IssueSeverity.Warning, Worst(issues));
        Assert.Contains(issues, i => i.Message.Contains("does not exist yet"));
    }
}
