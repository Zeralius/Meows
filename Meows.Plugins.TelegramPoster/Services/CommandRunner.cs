using System.Diagnostics;

namespace Meows.Plugins.TelegramPoster.Services;

public sealed record CommandResult(bool Started, int ExitCode, string? FailureReason)
{
    public bool Succeeded => Started && ExitCode == 0;
}

/// <summary>Runs an external command once and hands back its output line by line.</summary>
public static class CommandRunner
{
    public static async Task<CommandResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory,
        Action<string> onOutput,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken token = default)
    {
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
                info.Environment[key] = value;
        }

        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult(SafeExitCode(process));
        process.OutputDataReceived += (_, e) => Relay(e.Data, onOutput);
        process.ErrorDataReceived += (_, e) => Relay(e.Data, onOutput);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new CommandResult(false, -1, ex.Message);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await using (token.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // It probably just exited on its own. Nothing to report.
            }
        }))
        {
            var exitCode = await exited.Task.ConfigureAwait(true);
            return token.IsCancellationRequested
                ? new CommandResult(true, exitCode, "Cancelled.")
                : new CommandResult(true, exitCode, null);
        }
    }

    private static void Relay(string? line, Action<string> onOutput)
    {
        if (!string.IsNullOrWhiteSpace(line))
            onOutput(line);
    }

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }
}
