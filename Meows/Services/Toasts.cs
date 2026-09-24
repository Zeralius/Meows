using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;

namespace Meows.Services;

/// <summary>How Meows says something outside its window on this machine, decided once at startup.</summary>
public enum ToastSurface
{
    /// <summary>A Windows notification, with buttons, in the Action Center afterwards.</summary>
    Toast,

    /// <summary>A tray balloon, which Windows 10 and 11 show as a notification without buttons.</summary>
    Balloon,

    /// <summary>Not Windows, or nothing worked: said in the window only.</summary>
    None,
}

/// <summary>
/// Saying it outside the window. A real toast needs an AppUserModelID and a Start menu shortcut
/// carrying it; an installed Meows writes both into the user's own Start menu and registry, and a
/// portable folder on a stick writes neither and uses a tray balloon instead. Which one is decided
/// once at startup and said on the Settings tab, never failed quietly.
///
/// A toast's buttons open <c>meows:act/&lt;id&gt;</c>, which starts Meows with that argument; the
/// running Meows hears it through the single-instance pipe and presses the button it stands for.
/// </summary>
public static class Toasts
{
    public const string AppId = "Meows.Desktop";
    public const string Scheme = "meows";
    public const string ActPrefix = "meows:act/";

    public static ToastSurface Surface { get; private set; } = ToastSurface.None;

    /// <summary>Balloons still showing in this process, so a --do run can wait for them before it exits.</summary>
    private static readonly List<Task> Pending = [];

    /// <summary>
    /// Works out the surface, writing the shortcut and the protocol for an installed Meows. Safe
    /// to call more than once; the answer is the same.
    /// </summary>
    public static ToastSurface Prepare(bool portable, Action<string> log)
    {
        if (!OperatingSystem.IsWindows() || Environment.ProcessPath is not { } exe)
            return Surface = ToastSurface.None;
        if (portable)
            return Surface = ToastSurface.Balloon;

        try
        {
            EnsureShortcut(exe);
            EnsureProtocol(exe);
            return Surface = ToastSurface.Toast;
        }
        catch (Exception ex)
        {
            log($"Toasts fall back to tray balloons: {ex.Message}");
            return Surface = ToastSurface.Balloon;
        }
    }

    /// <summary>
    /// Says it. Buttons only reach a toast; a balloon has one line and no buttons, and clicking
    /// it does nothing more than a notification would. Never throws.
    /// </summary>
    public static void Show(string title, string text, bool trouble, Action<string> log, IReadOnlyList<(string Label, string Argument)>? buttons = null)
    {
        try
        {
            switch (Surface)
            {
                case ToastSurface.Toast:
                    if (!ShowToast(title, text, buttons ?? []))
                        ShowBalloon(title, text, trouble, log);
                    break;
                case ToastSurface.Balloon:
                    ShowBalloon(title, text, trouble, log);
                    break;
            }
        }
        catch (Exception ex)
        {
            log($"Could not say it outside the window: {ex.Message}");
        }
    }

    /// <summary>For a process about to exit: lets the balloons it raised be seen first.</summary>
    public static void Settle(TimeSpan most)
    {
        Task[] waiting;
        lock (Pending)
            waiting = [.. Pending];
        if (waiting.Length > 0)
            Task.WaitAll(waiting, most);
    }

    /// <summary>The toast's XML, escaped: a title or a file name with an ampersand must not break it.</summary>
    public static string ToastXml(string title, string text, IReadOnlyList<(string Label, string Argument)> buttons)
    {
        var xml = new StringBuilder();
        xml.Append($"<toast launch=\"{Scheme}:open\" activationType=\"protocol\">");
        xml.Append("<visual><binding template=\"ToastGeneric\">");
        xml.Append($"<text>{SecurityElement.Escape(title)}</text>");
        if (text.Length > 0)
            xml.Append($"<text>{SecurityElement.Escape(text)}</text>");
        xml.Append("</binding></visual>");
        if (buttons.Count > 0)
        {
            xml.Append("<actions>");
            foreach (var (label, argument) in buttons.Take(5))
                xml.Append($"<action content=\"{SecurityElement.Escape(label)}\" activationType=\"protocol\" arguments=\"{SecurityElement.Escape(argument)}\" />");
            xml.Append("</actions>");
        }
        xml.Append("</toast>");
        return xml.ToString();
    }

    /// <summary>
    /// Through Windows PowerShell, which can reach the notification API without Meows carrying
    /// the Windows SDK projection for one call. The XML goes in as a base64 string, so nothing in
    /// a title can become part of the script.
    /// </summary>
    private static bool ShowToast(string title, string text, IReadOnlyList<(string Label, string Argument)> buttons)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(ToastXml(title, text, buttons)));
        var script =
            "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] > $null;" +
            "[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] > $null;" +
            $"$x = New-Object Windows.Data.Xml.Dom.XmlDocument; $x.LoadXml([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{payload}')));" +
            $"[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('{AppId}').Show((New-Object Windows.UI.Notifications.ToastNotification $x))";

        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var argument in (string[])["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
                     Convert.ToBase64String(Encoding.Unicode.GetBytes(script))])
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
        return process is not null;
    }

    // ---- the Start menu shortcut that carries the AppUserModelID ----

    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Meows.lnk");

    /// <summary>Written once, and again when Meows has moved since, pointing at this exe and carrying the id.</summary>
    private static void EnsureShortcut(string exe)
    {
        var path = ShortcutPath;
        if (File.Exists(path) && string.Equals(Mouserless.TargetOf(path), exe, StringComparison.OrdinalIgnoreCase))
            return;

        var link = (IShellLinkW)new CShellLink();
        link.SetPath(exe);
        link.SetWorkingDirectory(Path.GetDirectoryName(exe)!);
        link.SetDescription("Meows");

        var store = (IPropertyStore)link;
        var key = new PropertyKey(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
        var value = new PropVariant(AppId);
        try
        {
            Check(store.SetValue(ref key, ref value));
            Check(store.Commit());
        }
        finally
        {
            value.Clear();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ((IPersistFile)link).Save(path, true);
    }

    /// <summary>meows: under the user's own classes, so a toast's button can start Meows with its argument. No administrator.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void EnsureProtocol(string exe)
    {
        using var root = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Scheme}");
        root.SetValue("", "URL:Meows");
        root.SetValue("URL Protocol", "");
        using var command = root.CreateSubKey(@"shell\open\command");
        command.SetValue("", $"\"{exe}\" \"%1\"");
    }

    private static void Check(int hresult)
    {
        if (hresult < 0)
            Marshal.ThrowExceptionForHR(hresult);
    }

    /// <summary>Where a shortcut points, read by the shell's own interface since this is the shell's own shortcut.</summary>
    private static class Mouserless
    {
        public static string? TargetOf(string path)
        {
            try
            {
                var link = (IShellLinkW)new CShellLink();
                ((IPersistFile)link).Load(path, 0);
                var buffer = new StringBuilder(1024);
                link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0);
                return buffer.ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr findData, int flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int show);
        void SetShowCmd(int show);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int max, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, int reserved);
        void Resolve(IntPtr hwnd, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010B-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid id);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, int mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string file, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string file);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey(Guid format, uint id)
    {
        public Guid Format = format;
        public uint Id = id;
    }

    /// <summary>A PROPVARIANT holding one wide string, which is all an AppUserModelID is.</summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] private ushort _type;
        [FieldOffset(8)] private IntPtr _pointer;

        public PropVariant(string value)
        {
            _type = 31; // VT_LPWSTR
            _pointer = Marshal.StringToCoTaskMemUni(value);
        }

        public void Clear()
        {
            if (_pointer != IntPtr.Zero)
                Marshal.FreeCoTaskMem(_pointer);
            _pointer = IntPtr.Zero;
        }
    }

    // ---- the tray balloon, for a portable Meows or when a toast will not go ----

    /// <summary>
    /// A notification-area icon of its own for a few seconds, carrying the balloon, on a thread
    /// of its own so nothing waits for it. Windows 10 and 11 show the balloon as a notification
    /// under Meows' name and icon.
    /// </summary>
    private static void ShowBalloon(string title, string text, bool trouble, Action<string> log)
    {
        if (!OperatingSystem.IsWindows())
            return;
        var shown = Task.Factory.StartNew(() =>
        {
            var window = IntPtr.Zero;
            var data = new NotifyIconData();
            try
            {
                window = CreateWindowExW(0, "STATIC", "Meows", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (window == IntPtr.Zero)
                    return;
                data = new NotifyIconData
                {
                    Size = (uint)Marshal.SizeOf<NotifyIconData>(),
                    Window = window,
                    Id = 0x4D45,
                    Flags = IconFlag | TipFlag | InfoFlag,
                    Icon = Environment.ProcessPath is { } exe ? ExtractIconW(IntPtr.Zero, exe, 0) : IntPtr.Zero,
                    Tip = "Meows",
                    InfoTitle = Clip(title, 63),
                    Info = Clip(text.Length == 0 ? title : text, 255),
                    InfoFlags = trouble ? WarningIcon : InfoIcon,
                };
                if (!Shell_NotifyIconW(Add, ref data))
                {
                    log("The tray would not take a balloon.");
                    return;
                }
                Thread.Sleep(TimeSpan.FromSeconds(8));
            }
            finally
            {
                if (window != IntPtr.Zero)
                {
                    Shell_NotifyIconW(Delete, ref data);
                    DestroyWindow(window);
                }
                if (data.Icon != IntPtr.Zero)
                    DestroyIcon(data.Icon);
            }
        }, TaskCreationOptions.LongRunning);

        lock (Pending)
        {
            Pending.RemoveAll(t => t.IsCompleted);
            Pending.Add(shown);
        }
    }

    private static string Clip(string text, int most) => text.Length <= most ? text : text[..(most - 1)] + "…";

    private const uint Add = 0, Delete = 2;
    private const uint IconFlag = 0x2, TipFlag = 0x4, InfoFlag = 0x10;
    private const uint InfoIcon = 0x1, WarningIcon = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Item;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIconW(IntPtr instance, string file, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}

/// <summary>
/// The buttons of toasts already raised, so the one pressed can be found when its argument comes
/// back through a second start of Meows. Kept for the life of the process, the last hundred.
/// </summary>
public sealed class ToastButtons
{
    private readonly Dictionary<string, Action> _waiting = [];
    private readonly Queue<string> _order = new();

    public string Remember(Action press)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        _waiting[id] = press;
        _order.Enqueue(id);
        while (_order.Count > 100)
            _waiting.Remove(_order.Dequeue());
        return Toasts.ActPrefix + id;
    }

    /// <summary>Presses the button an argument stands for. False when it is not one, or too old to know.</summary>
    public bool Press(string argument)
    {
        if (!argument.StartsWith(Toasts.ActPrefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var id = argument[Toasts.ActPrefix.Length..].TrimEnd('/');
        if (!_waiting.Remove(id, out var press))
            return false;
        press();
        return true;
    }
}
