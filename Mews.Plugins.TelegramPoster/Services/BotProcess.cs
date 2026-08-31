using System.Diagnostics;

namespace Mews.Plugins.TelegramPoster.Services;

/// <summary>
/// Runs `python bot.py` and watches it. The bot is still the only thing talking to Telegram.
/// We start it, stop it, and pass on whatever it prints.
/// </summary>
public sealed class BotProcess : IDisposable
{
    private readonly object _gate = new();
    private Process? _process;

    /// <summary>Every line the bot writes, stdout and stderr both.</summary>
    public event Action<string>? OutputReceived;

    /// <summary>Fires whenever it ends, for whatever reason, with the exit code.</summary>
    public event Action<int>? Exited;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return _process is { HasExited: false };
        }
    }

    public int? ProcessId
    {
        get
        {
            lock (_gate)
            {
                try
                {
                    return _process is { HasExited: false } p ? p.Id : null;
                }
                catch (InvalidOperationException)
                {
                    return null;
                }
            }
        }
    }

    /// <summary>Worth trying in this order on Windows.</summary>
    public static IReadOnlyList<string> DefaultPythonCandidates => ["python", "py", "python3"];

    public void Start(string pythonPath, BotWorkspace workspace)
    {
        lock (_gate)
        {
            if (_process is { HasExited: false })
                throw new InvalidOperationException("The bot is already running.");

            if (!workspace.LooksValid)
                throw new InvalidOperationException($"No bot.py / config.json under {workspace.Root}.");

            var info = new ProcessStartInfo
            {
                FileName = pythonPath,
                WorkingDirectory = workspace.Root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // -u or Python buffers into the pipe and the log arrives in bursts.
            info.ArgumentList.Add("-u");
            info.ArgumentList.Add(workspace.BotScriptPath);

            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => Relay(e.Data);
            process.ErrorDataReceived += (_, e) => Relay(e.Data);
            process.Exited += (_, _) =>
            {
                var code = SafeExitCode(process);
                OutputReceived?.Invoke($"-- bot exited with code {code} --");
                Exited?.Invoke(code);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;

            OutputReceived?.Invoke($"-- started {pythonPath} -u bot.py (pid {process.Id}) in {workspace.Root} --");
        }
    }

    public void Stop()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
        }

        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
            {
                // Nothing spawns children today, but kill the tree anyway.
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception ex)
        {
            OutputReceived?.Invoke($"-- could not stop the bot: {ex.Message} --");
        }
        finally
        {
            process.Dispose();
        }
    }

    private void Relay(string? line)
    {
        if (!string.IsNullOrWhiteSpace(line))
            OutputReceived?.Invoke(line);
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

    public void Dispose() => Stop();
}
