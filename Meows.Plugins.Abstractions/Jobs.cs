namespace Meows.Plugins.Abstractions;

/// <summary>
/// Work a plugin can do with no window: <c>Meows.exe --do weighin.measure</c>, one line in Task
/// Scheduler, so the nightly pass happens whether or not anyone opened Meows. Windows owns the
/// trigger and Meows owns the work; nothing here runs as a service or with nobody logged in.
/// Since 1.5.0.
/// </summary>
/// <param name="Id">Stable forever: scheduled tasks are written with it. Short and lower case, "measure", "copy".</param>
/// <param name="Label">What it does, as a verb phrase. A key from your strings catalogue, or plain text.</param>
/// <param name="Description">One sentence more, or null. A key or plain text.</param>
public sealed record PluginJob(string Id, string Label, string? Description = null)
{
    /// <summary>
    /// The same work the plugin also does on its own schedule while Meows is open. A job and a
    /// schedule are the same work with two owners, so a plugin that says this must ask
    /// <see cref="IMeowsHost.RunsFromOutside"/> before its own pass and stand down when Windows
    /// has been running the job, or the nightly pass happens twice.
    /// </summary>
    public bool StandsInForSchedule { get; init; }
}

/// <summary>
/// What a job is given: everything the dormant host gives, and the few things a job needs that
/// looking does not. It runs in its own process with no window and no UI thread, beside a Meows
/// that may well be open, so it touches nothing a view model owns.
/// </summary>
public interface IMeowsJobHost : IMeowsDormantHost
{
    /// <summary>A job is doing work, so unlike a dormant plugin it may save what it keeps.</summary>
    void SaveSettings<T>(T settings) where T : class;

    /// <summary>Into the job's log, which is where a scheduled task's output would otherwise vanish.</summary>
    void Log(string message);

    /// <summary>Where it has got to, printed as it goes when someone is watching the terminal.</summary>
    void Report(string status);

    /// <summary>
    /// Something worth saying with no window to say it in: a Windows notification when the shell
    /// can raise one, the log either way. Trouble is for something that wants doing.
    /// </summary>
    void Notify(string title, string text, bool isTrouble = false);
}

/// <summary>Thrown by a job that will not run as asked: an unknown id, nothing set up to do, a disk not there. The message is shown as is.</summary>
public sealed class JobDeclinedException(string message) : Exception(message);
