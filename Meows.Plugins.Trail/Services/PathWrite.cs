using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Meows.Plugins.Trail.Services;

/// <summary>What a tidy would do, worked out before anything is written.</summary>
/// <param name="Before">The stored value as it is now, which is what the backup holds.</param>
/// <param name="After">What it would become.</param>
/// <param name="Removed">The entries that would go, in the words they are stored in.</param>
public sealed record PathEdit(string Before, string After, IReadOnlyList<string> Removed)
{
    public bool ChangesAnything => !string.Equals(Before, After, StringComparison.Ordinal);
}

/// <summary>
/// The half of this plugin that writes, kept in a file of its own because it is the half that
/// can do harm. A bad PATH does not break a terminal, it breaks the logon, so three rules hold
/// throughout: the user's half only, never the machine's; a copy of the old value on disk before
/// the new one goes in; and the raw form preserved, so <c>%JAVA_HOME%\bin</c> is still following
/// a variable afterwards rather than frozen to wherever it pointed today.
/// </summary>
public static class PathWrite
{
    private const string UserKey = "Environment";

    /// <summary>
    /// What removing these entries would leave. Worked out from the stored string rather than
    /// from what is on screen, so an entry that changed since the tab was last read cannot be
    /// removed by position.
    /// </summary>
    public static PathEdit Plan(string? stored, IEnumerable<PathEntry> removing)
    {
        var before = stored ?? "";
        var drop = removing
            .Where(e => e.Scope == PathScope.User)
            .Select(e => e.Raw)
            .ToList();

        var kept = new List<string>();
        var removed = new List<string>();
        var remaining = new List<string>(drop);

        foreach (var raw in PathRead.Split(before))
        {
            // One removal per matching entry, so asking to drop one of two identical entries
            // drops one of them rather than both.
            var index = remaining.FindIndex(d => string.Equals(d, raw, StringComparison.Ordinal));
            if (index >= 0)
            {
                remaining.RemoveAt(index);
                removed.Add(raw);
                continue;
            }

            kept.Add(raw);
        }

        return new PathEdit(before, string.Join(';', kept), removed);
    }

    /// <summary>
    /// Writes the old value somewhere it can be read back from, and returns where it went.
    /// Called before every change, and a change is refused if this fails: an edit with no way
    /// back is the thing this plugin must never do.
    /// </summary>
    public static string Backup(string dataDirectory, string before)
    {
        Directory.CreateDirectory(dataDirectory);
        var file = Path.Combine(dataDirectory, $"path-{DateTime.Now:yyyy-MM-dd-HHmmss}.txt");
        File.WriteAllText(file, before);
        return file;
    }

    /// <summary>
    /// Puts the new value in the user's environment as <c>REG_EXPAND_SZ</c>, which is what it
    /// was. <see cref="Environment.SetEnvironmentVariable(string,string,EnvironmentVariableTarget)"/>
    /// writes a plain string instead, and a PATH that is no longer expandable stops following
    /// every variable in it from that moment on, quietly.
    /// </summary>
    public static string? Apply(string after)
    {
        if (!OperatingSystem.IsWindows())
            return "Only Windows keeps PATH this way.";

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UserKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(UserKey);
            key.SetValue("Path", after, RegistryValueKind.ExpandString);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }

        Announce();
        return null;
    }

    /// <summary>Puts a backup back, whatever has happened to PATH since.</summary>
    public static string? Restore(string backupFile)
    {
        string before;
        try
        {
            before = File.ReadAllText(backupFile);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }

        return Apply(before);
    }

    private const int HWND_BROADCAST = 0xFFFF;
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SendMessageTimeoutW(
        nint hWnd, uint msg, nint wParam, string lParam, uint flags, uint timeout, out nint result);

    /// <summary>
    /// Tells everything already running that the environment changed. Without this the registry
    /// holds the new value and every open program, Explorer included, carries on with the old
    /// one until the next logon, which looks exactly like the edit not having worked.
    ///
    /// Programs that were started before the change still keep their own copy whatever is
    /// broadcast; only ones that ask again get the new list. Worth saying on the tab rather than
    /// leaving somebody to wonder why their terminal disagrees.
    /// </summary>
    private static void Announce()
    {
        try
        {
            SendMessageTimeoutW(HWND_BROADCAST, WM_SETTINGCHANGE, nint.Zero, "Environment",
                SMTO_ABORTIFHUNG, 2000, out _);
        }
        catch (Exception)
        {
            // The write is what mattered and it has happened. The next logon picks it up either way.
        }
    }
}
