namespace Meows.Plugins.Abstractions;

/// <summary>
/// How much a log line matters. Info is the trail you would want when something misbehaves;
/// Warning is something a person might want to know; Error is something that went wrong. The
/// Log tab surfaces the last two and lets each plugin be turned down to them, or to nothing.
/// </summary>
public enum LogLevel
{
    Info,
    Warning,
    Error,
}
