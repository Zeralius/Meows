using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;
using Meows.Plugins.Abstractions;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Meows.Services;

/// <summary>What reaches Windows while the window is not in front.</summary>
public static class ToastModes
{
    /// <summary>Nothing. The panel and the tray's dot, as before.</summary>
    public const string Off = "off";

    /// <summary>
    /// The default: a condition when it appears or its words change, and an event that is a
    /// warning or an error. Collar's date that has come round is a condition at the Info level,
    /// so this is about kind rather than severity: an Info event is usually "finished", and
    /// finished is not worth leaving your work for.
    /// </summary>
    public const string Wanted = "wanted";

    /// <summary>Every event too, the finished ones included.</summary>
    public const string Everything = "everything";

    public static readonly string[] All = [Off, Wanted, Everything];

    public static string Tidy(string? mode) =>
        All.Contains(mode?.Trim().ToLowerInvariant()) ? mode!.Trim().ToLowerInvariant() : Wanted;
}

/// <summary>
/// Which notifications are worth a toast. Separate from Windows so it can be tested without
/// one, because this is the part that decides whether Meows is useful from the tray or merely
/// noisy, and the difference is one line.
/// </summary>
public static class ToastRule
{
    /// <param name="replaced">The condition this one took the place of under the same key, or null.</param>
    /// <param name="windowInFront">Whether the window is visible and active: the panel is then being looked at.</param>
    public static bool ShouldToast(NotificationItem item, NotificationItem? replaced, string mode, bool windowInFront)
    {
        mode = ToastModes.Tidy(mode);
        if (mode == ToastModes.Off || windowInFront)
            return false;

        if (item.IsCondition)
        {
            // A condition re-set with the same words on every pass is not news. Collar looks at
            // its dates daily and sets the same "2 are due" each time; that is one toast, and a
            // second one only when it becomes "3 are due".
            return replaced is null
                   || replaced.Title != item.Title
                   || replaced.Message != item.Message
                   || replaced.Severity != item.Severity;
        }

        return mode == ToastModes.Everything || item.Severity >= NotificationSeverity.Warning;
    }
}

/// <summary>What a pressed toast was pointing at: a condition by its key, or an event by its id.</summary>
/// <param name="Button">Which of the notification's buttons, or -1 for the toast itself.</param>
public sealed record ToastTarget(string? Source, string? ConditionKey, string? EventId, int Button)
{
    public bool IsCondition => ConditionKey is not null;

    public bool IsBody => Button < 0;
}

/// <summary>
/// The toast itself, as XML, and the arguments its buttons carry back.
///
/// A condition's toast is named for the condition rather than for the item, because the item
/// is replaced every time the plugin re-sets it: a button on yesterday's "2 are due" has to
/// reach today's, not a notification that no longer exists.
/// </summary>
public static class ToastContent
{
    /// <summary>Windows shows five buttons at most and the notification centre allows more.</summary>
    public const int MaxButtons = 5;

    public const string Group = "meows";

    /// <summary>
    /// The name Windows files the toast under, which is also how it is taken back down. Short and
    /// stable: a condition keeps its tag across every re-set, an event has its own.
    /// </summary>
    public static string Tag(NotificationItem item) =>
        item.IsCondition ? "c-" + Hash(item.Source + "\n" + item.ConditionKey) : "e-" + item.Id;

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..24].ToLowerInvariant();

    public static string Arguments(NotificationItem item, int button) =>
        item.IsCondition
            ? $"c|{Uri.EscapeDataString(item.Source)}|{Uri.EscapeDataString(item.ConditionKey!)}|{button}"
            : $"e|{item.Id}|{button}";

    /// <summary>Null for anything that is not one of ours, which is then treated as a plain open.</summary>
    public static ToastTarget? Parse(string? arguments)
    {
        if (string.IsNullOrEmpty(arguments))
            return null;

        var parts = arguments.Split('|');
        try
        {
            return parts switch
            {
                ["c", var source, var key, var button] when int.TryParse(button, out var b) =>
                    new ToastTarget(Uri.UnescapeDataString(source), Uri.UnescapeDataString(key), null, b),
                ["e", var id, var button] when int.TryParse(button, out var b) =>
                    new ToastTarget(null, null, id, b),
                _ => null,
            };
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The toast's XML. Built as a document rather than glued together as a string, so a title
    /// holding an ampersand or a quote is text rather than a toast Windows silently refuses.
    /// </summary>
    public static string Xml(NotificationItem item, string sourceName)
    {
        var binding = new XElement("binding", new XAttribute("template", "ToastGeneric"),
            new XElement("text", item.Title));
        if (item.HasMessage)
            binding.Add(new XElement("text", item.Message));
        binding.Add(new XElement("text", new XAttribute("placement", "attribution"), sourceName));

        var toast = new XElement("toast",
            new XAttribute("launch", Arguments(item, -1)),
            new XElement("visual", binding));

        // A warning stays up until it is dealt with rather than sliding away after a few seconds.
        if (item.Severity >= NotificationSeverity.Warning)
            toast.Add(new XAttribute("scenario", "reminder"));

        var buttons = item.Actions.Take(MaxButtons).Select((action, i) =>
            new XElement("action",
                new XAttribute("content", action.Label),
                new XAttribute("arguments", Arguments(item, i)),
                new XAttribute("activationType", "foreground"))).ToList();

        // A reminder with no buttons cannot be dismissed on some builds of Windows, so it is
        // only a reminder when it has something to press.
        if (buttons.Count == 0)
            toast.Attribute("scenario")?.Remove();
        else
            toast.Add(new XElement("actions", buttons));

        return toast.ToString(SaveOptions.DisableFormatting);
    }
}

/// <summary>Why toasts are or are not reaching Windows, for the Settings tab to say.</summary>
public enum ToastState
{
    Ready,
    Off,
    Portable,
    DisabledByWindows,
    Unavailable,
}

/// <summary>
/// Windows toasts, straight against WinRT.
///
/// No toolkit. The usual one brings System.Drawing.Common 4.7.0, which has a critical advisory,
/// and what it adds beyond this is a COM server registered against the exe's path so that a
/// toast can relaunch Meows after it has quit. Meows lives in the tray: while it runs, a toast's
/// own Activated event arrives in-process, which covers what the buttons are for. The price is
/// that a toast clicked after Meows has quit does nothing, so quitting takes Meows' toasts out of
/// the action centre rather than leaving buttons behind that would press nothing.
///
/// Registered with one key under HKCU, which names the app and its icon. Not in a portable copy,
/// which promises to write nothing to the profile.
/// </summary>
public sealed class WindowsToasts
{
    public const string AppId = "Zeralius.Meows";

    private readonly string _dataFolder;
    private readonly Action<string> _log;

    /// <summary>
    /// The toasts on screen or in the action centre, by tag. Held because the Activated event is
    /// on the object: let it be collected and the buttons stop answering.
    /// </summary>
    private readonly Dictionary<string, ToastNotification> _live = new(StringComparer.Ordinal);

    private bool _registered;
    private bool _saidWhy;

    /// <summary>ERROR_NOT_FOUND as an HRESULT, which is what Setting throws for an unpackaged app.</summary>
    private const int ElementNotFound = unchecked((int)0x80070490);

    /// <summary>
    /// Said once, with the reason. "Unavailable" on the Settings tab and nothing in the log would
    /// leave nobody able to tell a missing registration from a Windows that cannot do it.
    /// </summary>
    private void SayWhy(Exception ex)
    {
        if (_saidWhy)
            return;
        _log($"Toasts are unavailable: {ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}");
        _saidWhy = true;
    }

    public WindowsToasts(string dataFolder, Action<string> log)
    {
        _dataFolder = dataFolder;
        _log = log;
    }

    /// <summary>Raised on a thread of Windows' choosing when a toast or one of its buttons is pressed.</summary>
    public event Action<ToastTarget?>? Pressed;

    public ToastState State
    {
        get
        {
            if (ShellSettings.IsPortable)
                return ToastState.Portable;
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
                return ToastState.Unavailable;
            try
            {
                Register();
            }
            catch (Exception ex)
            {
                SayWhy(ex);
                return ToastState.Unavailable;
            }

            try
            {
                return ToastNotificationManager.CreateToastNotifier(AppId).Setting == NotificationSetting.Enabled
                    ? ToastState.Ready
                    : ToastState.DisabledByWindows;
            }
            catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == ElementNotFound)
            {
                // Windows cannot say whether toasts are on for an app with no Start menu
                // shortcut, and throws rather than answering, but it shows them regardless: asked
                // on this machine, Setting threw this and a toast shown straight after was held in
                // the action centre. So this is "unknown and working", not "unavailable". Only an
                // answer of disabled is taken as disabled.
                return ToastState.Ready;
            }
            catch (Exception ex)
            {
                SayWhy(ex);
                return ToastState.Unavailable;
            }
        }
    }

    /// <summary>
    /// Says who the toasts are from. Windows shows toasts from an unpackaged app only when it can
    /// name the app, and this key is what it names it from. Written once per run, so a moved
    /// folder is followed on the next start.
    /// </summary>
    private void Register()
    {
        if (_registered || !OperatingSystem.IsWindows())
            return;

        var icon = Path.Combine(_dataFolder, "toast.png");
        if (!File.Exists(icon))
        {
            try
            {
                using var source = Avalonia.Platform.AssetLoader.Open(new Uri("avares://Meows/Assets/tray.png"));
                using var target = File.Create(icon);
                source.CopyTo(target);
            }
            catch (Exception ex)
            {
                _log($"Could not write the toast icon: {ex.Message}");
            }
        }

        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + AppId);
        key.SetValue("DisplayName", "Meows");
        if (File.Exists(icon))
            key.SetValue("IconUri", icon);
        _registered = true;
    }

    public void Show(NotificationItem item, string sourceName)
    {
        if (State != ToastState.Ready)
            return;

        try
        {
            var document = new XmlDocument();
            document.LoadXml(ToastContent.Xml(item, sourceName));

            var tag = ToastContent.Tag(item);
            var toast = new ToastNotification(document) { Tag = tag, Group = ToastContent.Group };
            toast.Activated += (_, args) =>
                Pressed?.Invoke(ToastContent.Parse((args as ToastActivatedEventArgs)?.Arguments));
            toast.Dismissed += (_, _) => _live.Remove(tag);

            // One per tag: a changed condition replaces its own toast rather than stacking.
            Remove(tag);
            _live[tag] = toast;
            ToastNotificationManager.CreateToastNotifier(AppId).Show(toast);
        }
        catch (Exception ex)
        {
            _log($"A toast could not be shown: {ex.Message}");
        }
    }

    /// <summary>Takes one down from the screen and the action centre, if it is there.</summary>
    public void Remove(string tag)
    {
        _live.Remove(tag);
        try
        {
            ToastNotificationManager.History.Remove(tag, ToastContent.Group, AppId);
        }
        catch (Exception)
        {
            // Not there, or never was. Either way it is not there now.
        }
    }

    /// <summary>
    /// Every Meows toast out of the action centre. On quitting, because nothing would answer
    /// their buttons afterwards.
    /// </summary>
    public void Clear()
    {
        _live.Clear();
        try
        {
            if (_registered)
                ToastNotificationManager.History.Clear(AppId);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>How many of Meows' toasts Windows is holding, for the test button to report.</summary>
    public int InActionCentre
    {
        get
        {
            try
            {
                return ToastNotificationManager.History.GetHistory(AppId).Count;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}

/// <summary>
/// Between the notification centre and Windows: decides, shows, takes down, and turns a pressed
/// button back into the notification's own action.
/// </summary>
public sealed class ToastRelay : IDisposable
{
    private readonly NotificationCenter _center;
    private readonly WindowsToasts _toasts;
    private readonly Func<string> _mode;
    private readonly Func<bool> _windowInFront;
    private readonly Action _showWindow;
    private readonly Action<string> _log;

    public ToastRelay(
        NotificationCenter center,
        WindowsToasts toasts,
        Func<string> mode,
        Func<bool> windowInFront,
        Action showWindow,
        Action<string> log)
    {
        _center = center;
        _toasts = toasts;
        _mode = mode;
        _windowInFront = windowInFront;
        _showWindow = showWindow;
        _log = log;

        _center.Arrived += OnArrived;
        _center.Removed += OnRemoved;
        _toasts.Pressed += OnPressed;
    }

    private void OnArrived(NotificationItem item, NotificationItem? replaced)
    {
        if (!ToastRule.ShouldToast(item, replaced, _mode(), _windowInFront()))
            return;
        _toasts.Show(item, item.SourceName);
    }

    /// <summary>
    /// Dealt with in the app, so gone from the action centre too. A date marked done should not
    /// still be sitting in Windows asking to be marked done.
    /// </summary>
    private void OnRemoved(NotificationItem item)
    {
        // A condition's toast is shared by every item that has stood under its key, so it only
        // goes when the key is clear, not when one item was replaced by the next.
        if (item.IsCondition && _center.Condition(item.Source, item.ConditionKey!) is not null)
            return;
        _toasts.Remove(ToastContent.Tag(item));
    }

    private void OnPressed(ToastTarget? target) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Press(target));

    /// <summary>
    /// What pressing did. The toast itself opens the window; a button does what the same button
    /// in the panel does, and the window stays where it is, since "done" does not need a window.
    /// </summary>
    public void Press(ToastTarget? target)
    {
        var item = target switch
        {
            { IsCondition: true } => _center.Condition(target.Source!, target.ConditionKey!),
            { EventId: { } id } => _center.Find(id),
            _ => null,
        };

        if (target is null || target.IsBody || item is null || target.Button >= item.Actions.Count)
        {
            _showWindow();
            return;
        }

        var action = item.Actions[target.Button];
        try
        {
            action.Invoke();
        }
        catch (Exception ex)
        {
            _log($"The '{action.Label}' button from a toast failed: {ex.Message}");
            _showWindow();
            return;
        }

        if (action.DismissesAfter && item.CanDismiss)
            _center.Dismiss(item);
    }

    public void Dispose()
    {
        _center.Arrived -= OnArrived;
        _center.Removed -= OnRemoved;
        _toasts.Pressed -= OnPressed;
        _toasts.Clear();
    }
}
