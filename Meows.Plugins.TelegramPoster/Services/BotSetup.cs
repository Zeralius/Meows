namespace Meows.Plugins.TelegramPoster.Services;

/// <summary>Getting a bot checkout onto a machine that does not have one yet.</summary>
public static class BotSetup
{
    /// <summary>
    /// Prefilled in the setup panel. Fine to hardcode now the bot is public, and it means
    /// someone with Meows but not the link can still get a working checkout.
    /// </summary>
    public const string DefaultRepositoryUrl = "https://github.com/Zeralius/telegram-posting-bot.git";

    /// <summary>
    /// No terminal is attached, so a credential prompt would just hang forever. Fail instead.
    /// </summary>
    private static readonly Dictionary<string, string> NonInteractiveGit = new()
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
    };

    /// <summary>Next to the Meows checkout if we can find one, since that is the usual layout.</summary>
    public static string DefaultCloneDestination()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Meows.sln")) && dir.Parent is not null)
                return Path.Combine(dir.Parent.FullName, "telegram-posting-bot");
            dir = dir.Parent;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "telegram-posting-bot");
    }

    /// <summary>git clone would fail on a non-empty folder. Say so first, it reads better.</summary>
    public static string? DestinationProblem(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination))
            return "Pick a folder to clone into.";

        try
        {
            var full = Path.GetFullPath(destination);
            if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
                return $"{full} already exists and is not empty.";
            return null;
        }
        catch (Exception ex)
        {
            return $"That path cannot be used: {ex.Message}";
        }
    }

    public static Task<CommandResult> CloneAsync(
        string repositoryUrl,
        string destination,
        Action<string> log,
        CancellationToken token = default)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(destination));
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        return CommandRunner.RunAsync(
            "git",
            ["clone", "--progress", repositoryUrl, Path.GetFullPath(destination)],
            parent,
            log,
            NonInteractiveGit,
            token);
    }

    public static Task<CommandResult> InstallDependenciesAsync(
        string python,
        string botRoot,
        Action<string> log,
        CancellationToken token = default) =>
        CommandRunner.RunAsync(
            python,
            ["-m", "pip", "install", "-r", "requirements.txt"],
            botRoot,
            log,
            environment: null,
            token);

    /// <summary>Can this interpreter import what bot.py needs? Cheaper than guessing.</summary>
    public static async Task<bool> DependenciesPresentAsync(
        string python,
        string botRoot,
        CancellationToken token = default)
    {
        var result = await CommandRunner.RunAsync(
            python,
            ["-c", "import aiogram, apscheduler, dotenv"],
            botRoot,
            _ => { },
            environment: null,
            token).ConfigureAwait(true);

        return result.Succeeded;
    }

    /// <summary>
    /// Puts BOT_TOKEN into .env and leaves every other line alone. We never read the value
    /// back for display, only whether one is there.
    /// </summary>
    public static void WriteToken(string botRoot, string token)
    {
        var path = Path.Combine(botRoot, ".env");
        var lines = File.Exists(path)
            ? File.ReadAllLines(path).ToList()
            : [];

        var replaced = false;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].TrimStart().StartsWith("BOT_TOKEN=", StringComparison.OrdinalIgnoreCase))
                continue;
            lines[i] = "BOT_TOKEN=" + token;
            replaced = true;
            break;
        }

        if (!replaced)
            lines.Add("BOT_TOKEN=" + token);

        File.WriteAllLines(path, lines);
    }
}
