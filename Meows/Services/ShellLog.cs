using System.Collections.ObjectModel;
using Avalonia.Threading;
using Meows.Plugins.Abstractions;

namespace Meows.Services;

/// <summary>One line of the log, with its parts kept apart so the tab can filter on them.</summary>
public sealed record LogEntry(DateTime At, string Source, LogLevel Level, string Message)
{
    public string Text => $"{At:HH:mm:ss} [{Source}] {Message}";

    public bool IsWarning => Level == LogLevel.Warning;

    public bool IsError => Level == LogLevel.Error;
}

/// <summary>
/// The shared log. Bounded, so a chatty plugin cannot eat memory. Everything goes to the file;
/// what the pane and the Log tab show is theirs to decide, per source, which is what "quiet" is.
/// </summary>
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

    public DateTime StartedAt { get; } = DateTime.Now;

    public string? FilePath => _logFile;

    /// <summary>Every line this run, oldest first, on the UI thread.</summary>
    public ObservableCollection<LogEntry> Entries { get; } = new();

    /// <summary>The lines as text, for anything that only wants to read them.</summary>
    public IEnumerable<string> Lines => Entries.Select(e => e.Text);

    /// <summary>Raised on the UI thread after a line lands in <see cref="Entries"/>.</summary>
    public event Action<LogEntry>? Written;

    public void Write(string source, string message) => Write(source, message, LogLevel.Info);

    public void Write(string source, string message, LogLevel level)
    {
        var entry = new LogEntry(DateTime.Now, source, level, message);
        AppendToFile(level == LogLevel.Info ? entry.Text : $"{entry.At:HH:mm:ss} [{source}] {Tag(level)} {message}");

        Dispatcher.UIThread.Post(() =>
        {
            Entries.Add(entry);
            while (Entries.Count > MaxLines)
                Entries.RemoveAt(0);
            Written?.Invoke(entry);
        });
    }

    private static string Tag(LogLevel level) => level switch
    {
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        _ => "",
    };

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
