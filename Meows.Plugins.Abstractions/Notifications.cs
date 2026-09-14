namespace Meows.Plugins.Abstractions;

public enum NotificationSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// A button on a notification. Invoked on the UI thread. <paramref name="DismissesAfter"/> takes
/// an event notification down once the button has done its work, which is what a button on a
/// toast usually means; a condition is never dismissed this way, since only its plugin knows
/// whether it still applies.
/// </summary>
public sealed record NotificationAction(string Label, Action Invoke, bool DismissesAfter = false);

/// <summary>
/// One notification surface for the whole app. Report here rather than growing a banner in
/// your own tab, or nobody sees it while they are looking at something else.
/// </summary>
public interface IMeowsNotifications
{
    /// <summary>
    /// One-off events. Something finished, something failed. The user can dismiss these.
    /// </summary>
    void Post(NotificationSeverity severity, string title, string message = "", NotificationAction? action = null);

    /// <summary>
    /// The same, with as many buttons as the event deserves: *Done* and *Snooze*, *Shrink* and
    /// *Show me*. Two or three is the most anyone reads. Since 0.8.0; the default hands the
    /// first one to the older shape so a shell from before still shows something.
    /// </summary>
    void Post(NotificationSeverity severity, string title, string message, params NotificationAction[] actions) =>
        Post(severity, title, message, actions.Length > 0 ? actions[0] : null);

    /// <summary>
    /// Ongoing states like "Python is missing", not events. Same key replaces, so a repeated
    /// check will not pile up duplicates. Not user-dismissable: you clear it, because only
    /// you know whether it still applies. Always pair this with a ClearCondition call.
    /// </summary>
    void SetCondition(string key, NotificationSeverity severity, string title, string message = "",
        NotificationAction? action = null);

    /// <summary>A condition with several buttons. Since 0.8.0.</summary>
    void SetCondition(string key, NotificationSeverity severity, string title, string message, params NotificationAction[] actions) =>
        SetCondition(key, severity, title, message, actions.Length > 0 ? actions[0] : null);

    /// <summary>No-op if that key was never set.</summary>
    void ClearCondition(string key);
}
