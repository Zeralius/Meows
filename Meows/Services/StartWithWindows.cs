using Microsoft.Win32;

namespace Meows.Services;

/// <summary>What Windows will do with Meows at login.</summary>
public enum StartupState
{
    /// <summary>Nothing registered.</summary>
    Off,

    /// <summary>Registered, pointing at this build, and Windows is happy to run it.</summary>
    On,

    /// <summary>
    /// Registered, but pointing at a different copy of Meows. Usually the folder was moved, or
    /// this is a build being run from somewhere else.
    /// </summary>
    Elsewhere,

    /// <summary>
    /// Registered, but switched off in Windows' own startup list. Nothing here undoes that: a
    /// program quietly turning its own startup entry back on is what malware does.
    /// </summary>
    BlockedByWindows,

    /// <summary>Not Windows, or the registry would not answer.</summary>
    Unavailable,
}

/// <summary>Where it is registered and whether that is here.</summary>
public sealed record StartupRegistration(StartupState State, string? RegisteredPath)
{
    public bool IsOn => State is StartupState.On or StartupState.Elsewhere or StartupState.BlockedByWindows;
}

/// <summary>
/// Starting Meows when you log in, through the per-user Run key.
///
/// No admin rights, no scheduled task, no service, and it is one value somebody can delete by
/// hand if this ever gets it wrong. Windows' own startup list shows it and can switch it off,
/// which is the behaviour anyone would expect.
///
/// Nothing about this is stored in preferences.json. The registry is the state: keeping a copy
/// would only let the two disagree, and the one that decides is the registry.
/// </summary>
public static class StartWithWindows
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Where Windows records that somebody switched a startup entry off. The Run value stays
    /// exactly where it was, so reading only that would report a program as starting when it
    /// has not started in months.
    /// </summary>
    private const string ApprovedPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>The name of the value, and what shows in Windows' startup list.</summary>
    public const string EntryName = "Meows";

    /// <summary>
    /// This application's own exe, or null when Meows is not what is running.
    ///
    /// Checking for an .exe is not enough. A test runner is an exe, and so is the dotnet host,
    /// and registering either of those to start at login would be a memorable way to find that
    /// out. The name has to match the assembly this code lives in.
    /// </summary>
    public static string? ExecutablePath
    {
        get
        {
            if (Environment.ProcessPath is not { } path)
                return null;

            var assembly = typeof(StartWithWindows).Assembly;

            // Location is empty in a single file build, where the assembly name is all there is.
            var expected = assembly.Location.Length > 0
                ? Path.GetFileNameWithoutExtension(assembly.Location)
                : assembly.GetName().Name ?? "";

            return expected.Length > 0 && string.Equals(
                Path.GetFileNameWithoutExtension(path), expected, StringComparison.OrdinalIgnoreCase)
                ? path
                : null;
        }
    }

    /// <summary>
    /// Whether Windows has switched this entry off.
    ///
    /// The first byte carries it: bit zero set means disabled. The values seen in the wild are
    /// 2 and 6 for enabled and 3 and 7 for disabled, which is the same rule said twice.
    /// </summary>
    public static bool IsBlocked(byte[]? approval) =>
        approval is { Length: > 0 } && (approval[0] & 1) != 0;

    /// <summary>
    /// Whether a registered command line points at this exe.
    ///
    /// The stored value is a command line rather than a path, so it usually arrives quoted, and
    /// it may have been written by a copy that is no longer where it was.
    /// </summary>
    public static bool PointsAt(string? registered, string? executable)
    {
        if (registered is null || executable is null)
            return false;

        var quoted = registered.Trim();
        if (quoted.StartsWith('"'))
        {
            var end = quoted.IndexOf('"', 1);
            quoted = end > 0 ? quoted[1..end] : quoted[1..];
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(quoted), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // Not a path we can normalise, so it is certainly not this one.
            return false;
        }
    }

    public static StartupRegistration Read()
    {
        if (!OperatingSystem.IsWindows())
            return new StartupRegistration(StartupState.Unavailable, null);

        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunPath);
            if (run?.GetValue(EntryName) is not string registered || registered.Trim().Length == 0)
                return new StartupRegistration(StartupState.Off, null);

            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedPath);
            if (IsBlocked(approved?.GetValue(EntryName) as byte[]))
                return new StartupRegistration(StartupState.BlockedByWindows, registered);

            return new StartupRegistration(
                PointsAt(registered, ExecutablePath) ? StartupState.On : StartupState.Elsewhere,
                registered);
        }
        catch (Exception)
        {
            return new StartupRegistration(StartupState.Unavailable, null);
        }
    }

    /// <summary>
    /// Adds or removes the entry, pointing it at whichever copy of Meows is asking. Returns the
    /// reason it did not work, or null.
    ///
    /// Switching it on always rewrites the value, so ticking it again after moving the folder is
    /// how you re-point it.
    /// </summary>
    public static string? Set(bool on)
    {
        if (!OperatingSystem.IsWindows())
            return "This only works on Windows.";

        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunPath, writable: true);
            if (run is null)
                return "The startup list could not be opened.";

            if (!on)
            {
                run.DeleteValue(EntryName, throwOnMissingValue: false);
                return null;
            }

            if (ExecutablePath is not { } exe)
                return "Meows cannot tell where it is running from.";

            // Quoted, because Windows splits an unquoted command line on spaces and Program
            // Files is where this will usually live.
            run.SetValue(EntryName, $"\"{exe}\"", RegistryValueKind.String);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
