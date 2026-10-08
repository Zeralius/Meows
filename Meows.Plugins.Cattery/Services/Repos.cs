using System.Diagnostics;
using System.Globalization;

namespace Meows.Plugins.Cattery.Services;

/// <summary>
/// One repository as git itself describes it. Read-only: nothing here ever runs a git command
/// that changes anything, so the tab cannot break a repository however it is used.
/// </summary>
public sealed record RepoState(
    string Path,
    string? Branch,
    int Changed,
    int Untracked,
    int Conflicted,
    int? Ahead,
    int? Behind,
    string? Upstream,
    DateTime? LastCommitUtc,
    string? Error)
{
    public string Name => System.IO.Path.GetFileName(Path);

    public bool IsDirty => Changed + Untracked + Conflicted > 0;

    /// <summary>Commits here that are nowhere else: ahead of the upstream, or a branch with no upstream at all.</summary>
    public bool HasUnpushed => Ahead > 0 || (Upstream is null && LastCommitUtc is not null && Branch is not null);

    public bool IsDetached => Branch is null && Error is null;
}

/// <summary>
/// Cattery's two jobs: find the repositories under a folder, and ask git about each one. Finding
/// stops at a repository rather than walking into it, and skips the folders that are never
/// projects (node_modules, bin, obj and the like), so a drive of projects is a quick walk.
/// </summary>
public static class Repos
{
    private static readonly HashSet<string> NeverProjects = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", ".vs", ".idea", "packages", "target", "dist", "build", "__pycache__", ".venv", "venv", "Library",
    };

    /// <summary>Every folder holding a .git (folder, or file for a worktree), up to a few levels down.</summary>
    public static IReadOnlyList<string> Find(string root, int depth = 4, CancellationToken token = default)
    {
        var found = new List<string>();
        var pending = new Stack<(DirectoryInfo Folder, int Depth)>();
        try
        {
            pending.Push((new DirectoryInfo(root), 0));
        }
        catch (Exception)
        {
            return found;
        }

        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (folder, level) = pending.Pop();
            try
            {
                var git = System.IO.Path.Combine(folder.FullName, ".git");
                if (Directory.Exists(git) || File.Exists(git))
                {
                    found.Add(folder.FullName);
                    continue;
                }
                if (level >= depth)
                    continue;
                foreach (var child in folder.EnumerateDirectories())
                {
                    if (child.Name.StartsWith('.') || NeverProjects.Contains(child.Name) ||
                        child.Attributes.HasFlag(FileAttributes.ReparsePoint) || child.Attributes.HasFlag(FileAttributes.System))
                        continue;
                    pending.Push((child, level + 1));
                }
            }
            catch (Exception)
            {
                // A folder that cannot be read has no repositories as far as anyone here can tell.
            }
        }

        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }

    /// <summary>Where git is: on PATH, or where Git for Windows puts itself. Null when neither.</summary>
    public static string? GitExecutable()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "git.exe", "git.cmd" } : ["git"];
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                try
                {
                    var candidate = System.IO.Path.Combine(folder.Trim('"'), name);
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch (Exception)
                {
                }
            }
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var special in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.LocalApplicationData })
            {
                var candidate = System.IO.Path.Combine(Environment.GetFolderPath(special), special == Environment.SpecialFolder.LocalApplicationData ? @"Programs\Git\cmd\git.exe" : @"Git\cmd\git.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// Asks git about one repository: git status --porcelain=v2 --branch for the branch, the
    /// counts and ahead and behind, and git log for the last commit's date. Both only read.
    /// </summary>
    public static RepoState Read(string git, string path, CancellationToken token = default)
    {
        var (status, statusError) = Run(git, path, token, "-c", "core.quotepath=false", "--no-optional-locks", "status", "--porcelain=v2", "--branch");
        if (status is null)
            return new RepoState(path, null, 0, 0, 0, null, null, null, null, statusError ?? "git status failed");

        var state = Parse(path, status);
        var (log, _) = Run(git, path, token, "--no-optional-locks", "log", "-1", "--format=%ct");
        if (log is not null && long.TryParse(log.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            state = state with { LastCommitUtc = DateTime.UnixEpoch.AddSeconds(seconds) };
        return state;
    }

    /// <summary>Reads porcelain v2 status. Separate so the parsing is tested without a repository.</summary>
    public static RepoState Parse(string path, string status)
    {
        string? branch = null;
        string? upstream = null;
        int? ahead = null, behind = null;
        int changed = 0, untracked = 0, conflicted = 0;

        foreach (var line in status.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var text = line.TrimEnd('\r');
            if (text.StartsWith("# branch.head ", StringComparison.Ordinal))
            {
                var head = text["# branch.head ".Length..];
                branch = head == "(detached)" ? null : head;
            }
            else if (text.StartsWith("# branch.upstream ", StringComparison.Ordinal))
                upstream = text["# branch.upstream ".Length..];
            else if (text.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                var parts = text["# branch.ab ".Length..].Split(' ');
                if (parts.Length == 2 && int.TryParse(parts[0].TrimStart('+'), out var a) && int.TryParse(parts[1].TrimStart('-'), out var b))
                {
                    ahead = a;
                    behind = b;
                }
            }
            else if (text.StartsWith("1 ", StringComparison.Ordinal) || text.StartsWith("2 ", StringComparison.Ordinal))
                changed++;
            else if (text.StartsWith("u ", StringComparison.Ordinal))
                conflicted++;
            else if (text.StartsWith("? ", StringComparison.Ordinal))
                untracked++;
        }

        return new RepoState(path, branch, changed, untracked, conflicted, ahead, behind, upstream, null, null);
    }

    private static (string? Output, string? Error) Run(string git, string folder, CancellationToken token, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(git)
            {
                WorkingDirectory = folder,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
            // Never a prompt for credentials or a pager: this runs where nobody can answer.
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
            start.Environment["GIT_PAGER"] = "cat";
            start.Environment["LC_ALL"] = "C";

            using var process = Process.Start(start);
            if (process is null)
                return (null, "git did not start");
            var output = process.StandardOutput.ReadToEndAsync(token);
            var error = process.StandardError.ReadToEndAsync(token);
            if (!process.WaitForExit(30_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                }
                return (null, "git took too long");
            }
            return process.ExitCode == 0 ? (output.Result, null) : (null, error.Result.Trim());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }
}
