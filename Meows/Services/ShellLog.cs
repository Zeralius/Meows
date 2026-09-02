using System.Collections.ObjectModel;
using Avalonia.Threading;

namespace Meows.Services;

/// <summary>The shared log. Bounded, so a chatty plugin cannot eat memory.</summary>
public sealed class ShellLog
{
    private const int MaxLines = 2000;

    private readonly object _fileGate = new();
    private readonly string? _logFile;

    public ShellLog(string? logFile = null)
    {
        _logFile = logFile;
        if (logFile is null)
            return;

        try
        {
            // Fresh each run. This is for "what just happened", not an audit trail.
            File.WriteAllText(logFile, $"--- Meows started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---{Environment.NewLine}");
        }
        catch (Exception)
        {
            _logFile = null;
        }
    }

    public ObservableCollection<string> Lines { get; } = new();

    public void Write(string source, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} [{source}] {message}";
        AppendToFile(line);

        Dispatcher.UIThread.Post(() =>
        {
            Lines.Add(line);
            while (Lines.Count > MaxLines)
                Lines.RemoveAt(0);
        });
    }

    private void AppendToFile(string line)
    {
        if (_logFile is null)
            return;

        try
        {
            lock (_fileGate)
                File.AppendAllText(_logFile, line + Environment.NewLine);
        }
        catch (Exception)
        {
            // The pane is what matters. The file is a convenience, so swallow this.
        }
    }
}
