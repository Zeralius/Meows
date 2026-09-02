using System.Text;

namespace Meows.Services;

/// <summary>
/// Last resort logging for exceptions nothing else caught.
///
/// Background work already reports its own faults, but that only covers work plugins hand to the
/// shell. Anything thrown on the UI thread, during startup, or from an unawaited task used to
/// close the window with nothing written anywhere, which is useless for an app people run from a
/// folder they unzipped.
///
/// This writes the exception somewhere findable and gets out of the way. It never tries to keep
/// the app running: by then the state is unknown.
/// </summary>
public static class CrashLog
{
    private static string? _file;
    private static Exception? _alreadyWritten;

    /// <summary>
    /// Starts listening. Called before the UI exists, so it takes a path rather than the shell
    /// log, which does not exist yet. Startup crashes are worth catching too.
    /// </summary>
    public static void Watch(string file)
    {
        _file = file;

        try
        {
            // Nothing has created the settings folder yet at this point.
            var folder = Path.GetDirectoryName(file);
            if (folder is { Length: > 0 })
                Directory.CreateDirectory(folder);
        }
        catch (Exception)
        {
            // If the folder cannot be created, writes fail quietly later. Better than throwing
            // from the crash handler.
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("unhandled", e.ExceptionObject as Exception);

        // A faulted task nobody awaited. Not fatal on modern .NET, which is why it is worth
        // logging: otherwise it fails silently.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("unobserved task", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>Records an exception that escaped. Never throws, whatever happens.</summary>
    public static void Write(string kind, Exception? error)
    {
        try
        {
            if (_file is null)
                return;

            // Main records and rethrows, and the rethrow hits the AppDomain handler, so one
            // crash arrives here twice. Log it once.
            if (error is not null && ReferenceEquals(error, _alreadyWritten))
                return;

            _alreadyWritten = error;

            var text = new StringBuilder()
                .AppendLine()
                .AppendLine($"--- {kind} exception {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---")
                .AppendLine(error?.ToString() ?? "No exception object was supplied.")
                .ToString();

            File.AppendAllText(_file, text);
        }
        catch (Exception)
        {
            // Failing to log a crash should not cause another one.
        }
    }
}
