namespace Meows.Plugins.Abstractions;

public enum NotificationSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>Optional button on a notification. Invoked on the UI thread.</summary>
public sealed record NotificationAction(string Label, Action Invoke);

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
    /// Ongoing states like "Python is missing", not events. Same key replaces, so a repeated
    /// check will not pile up duplicates. Not user-dismissable: you clear it, because only
    /// you know whether it still applies. Always pair this with a ClearCondition call.
    /// </summary>
    void SetCondition(string key, NotificationSeverity severity, string title, string message = "",
        NotificationAction? action = null);

    /// <summary>No-op if that key was never set.</summary>
    void ClearCondition(string key);
}
