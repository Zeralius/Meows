using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Meows.Plugins.Abstractions;

namespace Meows.Services;

/// <summary>
/// Meows in the notification area, so closing the window does not stop it.
///
/// This is the decision IDEAS.md kept postponing under "an app you close": Collar's dates,
/// Birdwatch's refresh and every background task now carry on while the window is hidden, and
/// the icon says when there is something to read. It is still not a service and still not
/// running when nobody is logged in; it is the window out of the way rather than the app gone.
///
/// Quitting is explicit, from the tray menu, and closing the window is hiding it, unless the
/// Settings tab says otherwise, in which case the close is a quit as before.
/// </summary>
public sealed class TrayPresence : IDisposable
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly Func<Window> _window;
    private readonly ShellPreferences _preferences;
    private readonly NotificationCenter _notifications;
    private readonly Action _quit;
    private readonly IMeowsText _text;

    private readonly WindowIcon _plain;
    private readonly WindowIcon _news;
    private readonly TrayIcon _tray;
    private readonly NativeMenuItem _open;
    private readonly NativeMenuItem _quitItem;
    private Window? _shown;
    private bool _quitting;

    public TrayPresence(
        IClassicDesktopStyleApplicationLifetime desktop,
        Func<Window> window,
        ShellPreferences preferences,
        NotificationCenter notifications,
        IMeowsText text,
        Action quit)
    {
        _desktop = desktop;
        _window = window;
        _preferences = preferences;
        _notifications = notifications;
        _text = text;
        _quit = quit;

        _plain = new WindowIcon(AssetLoader.Open(new Uri("avares://Meows/Assets/tray.png")));
        _news = new WindowIcon(AssetLoader.Open(new Uri("avares://Meows/Assets/tray-news.png")));

        _open = new NativeMenuItem();
        _open.Click += (_, _) => Show();
        _quitItem = new NativeMenuItem();
        _quitItem.Click += (_, _) => Quit();

        var menu = new NativeMenu();
        menu.Add(_open);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(_quitItem);

        _tray = new TrayIcon { Icon = _plain, Menu = menu, IsVisible = true };
        _tray.Clicked += (_, _) => Show();
        Relabel();
        Refresh();

        _notifications.Changed += Refresh;
        _text.PropertyChanged += (_, _) => Relabel();

        TrayIcon.SetIcons(Application.Current!, [_tray]);
    }

    /// <summary>The window, brought to the front, created on first showing when Meows started in the tray.</summary>
    public void Show()
    {
        if (_shown is null)
        {
            _shown = _window();
            _shown.Closing += OnClosing;
            _desktop.MainWindow = _shown;
        }

        if (_shown.WindowState == WindowState.Minimized)
            _shown.WindowState = WindowState.Normal;

        _shown.Show();
        _shown.Activate();
    }

    /// <summary>Closing the window hides it while the tray is wanted; otherwise it is the quit it always was.</summary>
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_quitting || !_preferences.CloseToTray)
        {
            if (!_quitting)
                Quit();
            return;
        }

        e.Cancel = true;
        _shown?.Hide();
    }

    public void Quit()
    {
        if (_quitting)
            return;
        _quitting = true;

        _quit();
        _desktop.Shutdown();
    }

    /// <summary>The icon says whether there is anything to read, and the tooltip says how much.</summary>
    private void Refresh()
    {
        var count = _notifications.Count;
        _tray.Icon = count > 0 ? _news : _plain;
        _tray.ToolTipText = count switch
        {
            0 => "Meows",
            1 => _text["tray.tip.one"],
            _ => _text.Format("tray.tip.many", count),
        };
    }

    private void Relabel()
    {
        _open.Header = _text["tray.open"];
        _quitItem.Header = _text["tray.quit"];
        Refresh();
    }

    public void Dispose()
    {
        _notifications.Changed -= Refresh;
        _tray.IsVisible = false;
        _tray.Dispose();
    }
}
