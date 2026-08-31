namespace Mews.Plugins.Abstractions;

/// <summary>Passed into your work so it can say where it has got to.</summary>
public interface IBackgroundContext
{
    /// <summary>Pass this to every async call, or deactivation cannot stop you.</summary>
    CancellationToken Token { get; }

    /// <summary>Shown verbatim in the Tasks panel, so write it for a human.</summary>
    void Report(string status);

    /// <summary>0 to 1, or null when you cannot tell how long it will take.</summary>
    void ReportProgress(double? fraction);
}

/// <summary>A handle on running work. Disposing it cancels.</summary>
public interface IBackgroundTask : IDisposable
{
    string Title { get; }

    bool IsRunning { get; }

    void Cancel();
}

/// <summary>
/// For folder watches, long imports, periodic scans. The shell owns the lifetime and cancels
/// everything on deactivation or shutdown, so you cannot leave a loop running behind you.
/// </summary>
public interface IMewsBackgroundWork
{
    /// <summary>Runs once. A fault becomes an error notification, not a crash.</summary>
    IBackgroundTask Run(string title, Func<IBackgroundContext, Task> work);

    /// <summary>
    /// Waits <paramref name="interval"/> after each pass finishes, not on a fixed clock, so
    /// passes never overlap. A slow one just pushes the next one back.
    /// </summary>
    IBackgroundTask Schedule(string title, TimeSpan interval, Func<IBackgroundContext, Task> work,
        bool runImmediately = true);
}
