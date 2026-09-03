using Meows.Plugins.Abstractions;
using Meows.Plugins.TelegramPoster.Model;
using Meows.Bot;

namespace Meows.Plugins.TelegramPoster.Services;

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
                MeowsText.Current["tp.issue.noname"]));
    }

    private static void ValidateChatId(GroupConfig group, IReadOnlyList<GroupConfig> others, List<GroupIssue> issues)
    {
        var chatId = group.ChatId?.Trim() ?? "";

        if (chatId.Length == 0)
        {
            issues.Add(new GroupIssue(IssueSeverity.Error,
                MeowsText.Current["tp.issue.nochatid"]));
            return;
        }

        if (IsPlaceholder(chatId))
        {
            // Bailing here also skips the duplicate check, which would otherwise fire on
            // every group of a fresh clone and point at the wrong thing.
            issues.Add(new GroupIssue(IssueSeverity.Error,
                MeowsText.Current["tp.issue.placeholder"]));
            return;
        }

        // Public channels can be @name. Everything else should be a number.
        if (!chatId.StartsWith('@'))
        {
            if (!long.TryParse(chatId, out var numeric))
                issues.Add(new GroupIssue(IssueSeverity.Warning,
                    MeowsText.Current["tp.issue.chatidshape"]));
            else if (numeric >= 0)
                issues.Add(new GroupIssue(IssueSeverity.Warning,
                    MeowsText.Current["tp.issue.chatidpositive"]));
        }

        var clash = others.FirstOrDefault(o => (o.ChatId?.Trim() ?? "") == chatId);
        if (clash is not null)
            issues.Add(new GroupIssue(IssueSeverity.Warning,
                MeowsText.Current.Format("tp.issue.chatidclash", Describe(clash))));
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
                MeowsText.Current["tp.issue.nofolder"]));
            return;
        }

        if (folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            issues.Add(new GroupIssue(IssueSeverity.Error, MeowsText.Current["tp.issue.badfolder"]));
            return;
        }

        string resolved;
        try
        {
            resolved = workspace.GroupFolder(group);
        }
        catch (Exception ex)
        {
            issues.Add(new GroupIssue(IssueSeverity.Error, MeowsText.Current.Format("tp.issue.unresolvable", ex.Message)));
            return;
        }

        if (!Directory.Exists(resolved))
        {
            // setup_folders will create it, so not fatal. Still worth saying, because a
            // typo just gives you an empty queue and no error.
            issues.Add(new GroupIssue(IssueSeverity.Warning,
                MeowsText.Current.Format("tp.issue.foldermissing", resolved)));
        }
        else if (!Directory.Exists(Path.Combine(resolved, "To_Send")))
        {
            issues.Add(new GroupIssue(IssueSeverity.Warning,
                MeowsText.Current["tp.issue.notosend"]));
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
                MeowsText.Current.Format("tp.issue.folderclash", Describe(clash))));
    }

    private static void ValidateContent(GroupConfig group, int queueCount, int archiveCount, List<GroupIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(group.Folder))
            return; // Already reported; counts would be meaningless.

        if (queueCount == 0 && archiveCount == 0)
            issues.Add(new GroupIssue(IssueSeverity.Warning,
                MeowsText.Current["tp.issue.nocontent"]));
    }

    private static string Describe(GroupConfig group) =>
        string.IsNullOrWhiteSpace(group.Name) ? "(unnamed group)" : group.Name;
}
