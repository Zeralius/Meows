using Meows.Plugins.TelegramPoster.Model;
using Meows.Plugins.TelegramPoster.Services;
using Meows.Bot;

namespace Meows.Tests;

/// <summary>
/// A throwaway bot checkout on disk. Several of these tests are about how the plugin reads
/// real folders, so faking the filesystem would test the fake instead.
/// </summary>
public sealed class TempWorkspace : IDisposable
{
    public TempWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "meows-tests-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, "bot.py"), "# placeholder");
        File.WriteAllText(Path.Combine(Root, "config.json"), """{ "groups": [] }""");
        Workspace = new BotWorkspace(Root);
    }

    public string Root { get; }

    public BotWorkspace Workspace { get; }

    public GroupConfig AddGroup(string name, string chatId = "-1004498007139", string folder = "")
    {
        folder = folder.Length == 0 ? "groups/" + name.ToLowerInvariant() : folder;
        Directory.CreateDirectory(Path.Combine(Root, folder.Replace('/', Path.DirectorySeparatorChar), "To_Send"));
        Directory.CreateDirectory(Path.Combine(Root, folder.Replace('/', Path.DirectorySeparatorChar), "Already_Sent"));
        return new GroupConfig
        {
            Name = name,
            ChatId = chatId,
            Folder = folder,
            Schedule = new ScheduleConfig { IntervalMinutes = 60 },
        };
    }

    /// <summary>
    /// Puts the groups into config.json. AddGroup only hands back an object and makes the
    /// folders, which is all the service level tests need, but anything driving a view model
    /// reads the config from disk the way the plugin does.
    /// </summary>
    public void WriteConfig(params GroupConfig[] groups)
    {
        Workspace.SaveConfig(new BotConfig { Groups = [.. groups] });
    }

    /// <summary>Writes a file into a group's queue with an explicit modified time.</summary>
    public string Queue(GroupConfig group, string fileName, DateTime modifiedUtc, byte[]? content = null)
    {
        var path = Path.Combine(Workspace.ToSendFolder(group), fileName);
        File.WriteAllBytes(path, content ?? [1, 2, 3, 4]);
        File.SetLastWriteTimeUtc(path, modifiedUtc);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temp folder is not worth failing a test run over.
        }
    }
}
