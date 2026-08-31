using Mews.Plugins.TelegramPoster.Model;

namespace Mews.Plugins.TelegramPoster.Services;

public enum IssueSeverity
{
    /// <summary>Looks wrong, but the bot will still run.</summary>
    Warning,

    /// <summary>The bot cannot post this group, or will not start.</summary>
    Error,
}

public sealed record GroupIssue(IssueSeverity Severity, string Message)
{
    public bool IsError => Severity == IssueSeverity.Error;
}

/// <summary>
/// Catches half-finished groups before the bot does.
///
/// name, chat_id and folder are errors rather than warnings because bot.py reads them with
/// bracket access. Miss one and the whole bot dies at startup, not just that group.
/// </summary>
public static class GroupValidator
{
    /// <summary>Straight out of cfgExample.json. They point at nothing.</summary>
    private static readonly string[] ExampleChatIds = ["-1001234567890", "-1009876543210"];

    /// <summary>
    /// A fresh clone ships placeholders, so this is the first thing a new user runs into.
    /// Worth calling out by name, otherwise REPLACE_WITH_CHAT_ID only reads as "malformed".
    /// </summary>
    private static bool IsPlaceholder(string chatId) =>
        ExampleChatIds.Contains(chatId) ||
        chatId.Contains("REPLACE", StringComparison.OrdinalIgnoreCase) ||
        chatId.Contains("YOUR_", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<GroupIssue> Validate(
        GroupConfig group,
        BotWorkspace workspace,
        IReadOnlyList<GroupConfig> otherGroups,
        int queueCount,
        int archiveCount)
    {
        var issues = new List<GroupIssue>();

        ValidateName(group, issues);
        ValidateChatId(group, otherGroups, issues);
        ValidateFolder(group, workspace, otherGroups, issues);
        ValidateContent(group, queueCount, archiveCount, issues);

        return issues;
    }

    private static void ValidateName(GroupConfig group, List<GroupIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(group.Name))
            issues.Add(new GroupIssue(IssueSeverity.Error,
                "No name set. The bot reads this key directly and will not start without it."));
    }

    private static void ValidateChatId(GroupConfig group, IReadOnlyList<GroupConfig> others, List<GroupIssue> issues)
    {
        var chatId = group.ChatId?.Trim() ?? "";

        if (chatId.Length == 0)
        {
            issues.Add(new GroupIssue(IssueSeverity.Error,
                "No chat ID yet. Forward a message from the group to @userinfobot to get it."));
            return;
        }

        if (IsPlaceholder(chatId))
        {
            // Bailing here also skips the duplicate check, which would otherwise fire on
            // every group of a fresh clone and point at the wrong thing.
            issues.Add(new GroupIssue(IssueSeverity.Error,
                "Chat ID is still a placeholder. Forward a message from the group to " +
                "@userinfobot to get the real one."));
            return;
        }

        // Public channels can be @name. Everything else should be a number.
        if (!chatId.StartsWith('@'))
        {
            if (!long.TryParse(chatId, out var numeric))
                issues.Add(new GroupIssue(IssueSeverity.Warning,
                    "Chat ID is neither a number nor an @channelname. Groups look like -1001234567890."));
            else if (numeric >= 0)
                issues.Add(new GroupIssue(IssueSeverity.Warning,
                    "Chat ID is positive, which is a private chat. Groups and channels are negative."));
        }

        var clash = others.FirstOrDefault(o => (o.ChatId?.Trim() ?? "") == chatId);
        if (clash is not null)
            issues.Add(new GroupIssue(IssueSeverity.Warning,
                $"Same chat ID as '{Describe(clash)}'. The bot keys its schedule by chat ID, " +
                "so only the last of them is scheduled and the other never posts."));
    }

    private static void ValidateFolder(
        GroupConfig group,
        BotWorkspace workspace,
        IReadOnlyList<GroupConfig> others,
        List<GroupIssue> issues)
    {
        var folder = group.Folder?.Trim() ?? "";

        if (folder.Length == 0)
        {
            issues.Add(new GroupIssue(IssueSeverity.Error,
                "No folder yet. Set something like groups/my_group, relative to the bot root."));
            return;
        }

        if (folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            issues.Add(new GroupIssue(IssueSeverity.Error, "Folder contains characters that are not valid in a path."));
            return;
        }

        string resolved;
        try
        {
            resolved = workspace.GroupFolder(group);
        }
        catch (Exception ex)
        {
            issues.Add(new GroupIssue(IssueSeverity.Error, $"Folder path cannot be resolved: {ex.Message}"));
            return;
        }

        if (!Directory.Exists(resolved))
        {
            // setup_folders will create it, so not fatal. Still worth saying, because a
            // typo just gives you an empty queue and no error.
            issues.Add(new GroupIssue(IssueSeverity.Warning,
                $"Folder does not exist yet. The bot will create it empty at startup: {resolved}"));
        }
        else if (!Directory.Exists(Path.Combine(resolved, "To_Send")))
        {
            issues.Add(new GroupIssue(IssueSeverity.Warning,
                "Folder exists but has no To_Send subfolder yet. The bot creates it at startup."));
        }

        var clash = others.FirstOrDefault(o =>
        {
            try
            {
                return !string.IsNullOrWhiteSpace(o.Folder) &&
                       string.Equals(workspace.GroupFolder(o), resolved, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        });

        if (clash is not null)
            issues.Add(new GroupIssue(IssueSeverity.Warning,
                $"Shares its folder with '{Describe(clash)}'. Whichever group posts first moves the file out of the queue."));
    }

    private static void ValidateContent(GroupConfig group, int queueCount, int archiveCount, List<GroupIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(group.Folder))
            return; // Already reported; counts would be meaningless.

        if (queueCount == 0 && archiveCount == 0)
            issues.Add(new GroupIssue(IssueSeverity.Warning,
                "Nothing in To_Send or Already_Sent, so this group has nothing to post."));
    }

    private static string Describe(GroupConfig group) =>
        string.IsNullOrWhiteSpace(group.Name) ? "(unnamed group)" : group.Name;
}
