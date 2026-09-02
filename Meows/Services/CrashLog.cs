using System.Text;

namespace Meows.Services;

/// <summary>
/// The last thing to run when nothing else caught it.
///
/// Background work already turns a fault into a notification, but that is only the work plugins
/// hand to the shell. Anything thrown on the UI thread, during startup, or from a task nobody
/// awaited went straight past everything and closed the window with no trace at all. For an app
/// people run out of a folder they unzipped, that means a crash they cannot report and nobody can
/// diagnose.
///
/// This writes what happened somewhere findable, then gets out of the way. It never tries to keep
/// the app alive: an unhandled exception means the state is already unknown.
/// </summary>
public static class CrashLog
{
    private static string? _file;
    private static Exception? _alreadyWritten;

    /// <summary>
    /// Starts listening. Called before the UI exists, so it takes a path rather than a log: the
    /// crashes worth catching include the ones that happen before anything is built.
    /// </summary>
    public static void Watch(string file)
    {
        _file = file;

        try
        {
            // Nothing has created the settings folder yet at this point, and a crash during
            // startup is exactly the one worth catching.
            var folder = Path.GetDirectoryName(file);
            if (folder is { Length: > 0 })
                Directory.CreateDirectory(folder);
        }
        catch (Exception)
        {
            // If the folder cannot be made, writes will fail quietly later. Still better than
            // throwing from the crash handler itself.
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("unhandled", e.ExceptionObject as Exception);

        // A faulted task whose exception nobody ever looked at. Not fatal by default on modern
        // .NET, which is exactly why it is worth writing down: it fails silently otherwise.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("unobserved task", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>Records something that got all the way out. Never throws, whatever happens.</summary>
    public static void Write(string kind, Exception? error)
    {
        try
        {
            if (_file is null)
                return;

            // Main catches, records and rethrows, and the rethrow reaches the handler below, so
            // one crash arrives here twice. The same exception is worth writing down once.
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
            // Failing to record a crash is not a reason to cause another one.
        }
    }
}
