using System.Text.RegularExpressions;

namespace Meows.Plugins.TelegramPoster.Services;

/// <summary>What we found, if anything.</summary>
public sealed record ToolProbeResult(bool Found, string? Command, string? Version)
{
    public static ToolProbeResult Missing => new(false, null, null);
}

/// <summary>
/// Are the tools we shell out to actually there? Better to know now than halfway through a
/// clone.
/// </summary>
public static partial class ToolProbe
{
    [GeneratedRegex(@"Python\s+(\d+\.\d+[\w.+]*)", RegexOptions.IgnoreCase)]
    private static partial Regex PythonVersion();

    [GeneratedRegex(@"git\s+version\s+(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex GitVersion();

    /// <summary>
    /// Saved interpreter first, then the usual names.
    ///
    /// Watch out for python3 on Windows: it is often an App Execution Alias that prints a
    /// localised "Python was not found" and exits 9009. That message still has the word
    /// Python in it, so match a version number instead or you will accept the stub.
    /// </summary>
    public static async Task<ToolProbeResult> FindPythonAsync(string? preferred, CancellationToken token = default)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(preferred))
            candidates.Add(preferred!);
        candidates.AddRange(BotProcess.DefaultPythonCandidates);

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var version = await VersionOfAsync(candidate, ["--version"], PythonVersion(), token).ConfigureAwait(true);
            if (version is not null)
                return new ToolProbeResult(true, candidate, version);
        }

        return ToolProbeResult.Missing;
    }

    public static async Task<ToolProbeResult> FindGitAsync(CancellationToken token = default)
    {
        var version = await VersionOfAsync("git", ["--version"], GitVersion(), token).ConfigureAwait(true);
        return version is not null ? new ToolProbeResult(true, "git", version) : ToolProbeResult.Missing;
    }

    private static async Task<string?> VersionOfAsync(
        string command,
        string[] arguments,
        Regex pattern,
        CancellationToken token)
    {
        var output = new List<string>();
        CommandResult result;
        try
        {
            result = await CommandRunner.RunAsync(
                command, arguments, null, output.Add, environment: null, token).ConfigureAwait(true);
        }
        catch (Exception)
        {
            return null;
        }

        if (!result.Succeeded)
            return null;

        var match = pattern.Match(string.Join(" ", output));
        return match.Success ? match.Value.Trim() : null;
    }
}
