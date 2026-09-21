using System.Diagnostics;
using System.Text;

namespace Meows.Plugins.Rehome.Services;

public sealed record RunResult(bool Started, int ExitCode, IReadOnlyList<string> Output, string? FailureReason)
{
    public bool Succeeded => Started && ExitCode == 0;

    public static RunResult NotStarted(string why) => new(false, -1, [], why);
}

/// <summary>Runs a command once, quietly, and hands back what it printed. Everything Rehome asks the machine goes through here.</summary>
public static class Run
{
    public static async Task<RunResult> CaptureAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken token, TimeSpan? timeout = null)
    {
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var lines = new List<string>();
        var gate = new object();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (gate) lines.Add(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (gate) lines.Add(e.Data); };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return RunResult.NotStarted(ex.Message);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(timeout ?? TimeSpan.FromMinutes(3));
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { }
            if (token.IsCancellationRequested)
                throw;
            return new RunResult(true, -1, Snapshot(), "timed out");
        }

        // The output streams drain a moment after the exit; WaitForExit() without a timeout waits for them.
        process.WaitForExit();
        return new RunResult(true, process.ExitCode, Snapshot(), null);

        IReadOnlyList<string> Snapshot()
        {
            lock (gate) return lines.ToList();
        }
    }

    /// <summary>A PowerShell one-liner, the way the Wi-Fi export, the Store list and the firmware key have to be asked.</summary>
    public static Task<RunResult> PowerShellAsync(string command, CancellationToken token, TimeSpan? timeout = null) =>
        CaptureAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command], token, timeout);

    /// <summary>Whether a command is on the PATH, without running it.</summary>
    public static bool OnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var extensions = new[] { "", ".exe", ".cmd", ".bat", ".ps1" };
        foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                try
                {
                    if (File.Exists(Path.Combine(folder.Trim(), fileName + extension)))
                        return true;
                }
                catch (Exception)
                {
                    // A PATH entry that is not a path.
                }
            }
        }
        return false;
    }
}
