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
    private readonly BackgroundTaskService? _background;
    private readonly Action _quit;
    private readonly IMeowsText _text;

    private readonly WindowIcon _plain;
    private readonly WindowIcon _news;
    private readonly WindowIcon _busy;
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
        Action quit,
        BackgroundTaskService? background = null)
    {
        _desktop = desktop;
        _window = window;
        _preferences = preferences;
        _notifications = notifications;
        _background = background;
        _text = text;
        _quit = quit;

        _plain = new WindowIcon(AssetLoader.Open(new Uri("avares://Meows/Assets/tray.png")));
        _news = new WindowIcon(AssetLoader.Open(new Uri("avares://Meows/Assets/tray-news.png")));
        _busy = new WindowIcon(AssetLoader.Open(new Uri("avares://Meows/Assets/tray-busy.png")));

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
        if (_background is not null)
            _background.Changed += Refresh;
        _text.PropertyChanged += (_, _) => Relabel();

        TrayIcon.SetIcons(Application.Current!, [_tray]);
    }

    /// <summary>The window, brought to the front, created on first showing when Meows started in the tray.</summary>
    /// <summary>Whether the window is open and in front, when a notification is already being seen.</summary>
    public bool IsWindowActive => _shown is { IsVisible: true, IsActive: true };

    public void Show()
    {
        if (_shown is null)
        {
            _shown = _window();
            _shown.Closing += OnClosing;
            _desktop.MainWindow = _shown;
            if (_shown.DataContext is ViewModels.MainWindowViewModel model)
            {
                model.Window = _shown;
                _shown.Opened += (_, _) => model.RestorePopOuts();
            }

            // Back where it was on this arrangement of screens, the way a popped-out tab is.
            if (WindowLayout.Of(_shown) is { } layout && _preferences.MainWindowPlaces.TryGetValue(layout, out var place))
                WindowLayout.Apply(_shown, place);
        }

        if (_shown.WindowState == WindowState.Minimized)
            _shown.WindowState = WindowState.Normal;

        _shown.Show();
        _shown.Activate();
    }

    /// <summary>Where the window is now, kept for the next time it is shown on these screens.</summary>
    private void RememberPlace()
    {
        if (_shown is null || WindowLayout.Of(_shown) is not { } layout || WindowLayout.PlaceOf(_shown) is not { } place)
            return;
        _preferences.MainWindowPlaces[layout] = place;
        if (_shown.DataContext is ViewModels.MainWindowViewModel model)
            model.SavePreferencesNow();
    }

    /// <summary>Closing the window hides it while the tray is wanted; otherwise it is the quit it always was.</summary>
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        RememberPlace();
        if (_quitting || !_preferences.CloseToTray)
        {
            if (!_quitting)
                Quit();
            return;
        }

        e.Cancel = true;
        if (_shown?.DataContext is ViewModels.MainWindowViewModel model)
            model.MarkSeen();
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

    /// <summary>
    /// The icon says whether there is anything to read, or failing that whether something is
    /// still working, and the tooltip says how much of each. A scan running while the window is
    /// closed used to look exactly like nothing happening.
    /// </summary>
    private void Refresh()
    {
        var count = _notifications.Count;
        var running = _background?.RunningCount ?? 0;
        _tray.Icon = count > 0 ? _news : running > 0 ? _busy : _plain;

        var news = count switch
        {
            0 => "",
            1 => _text["tray.tip.one"],
            _ => _text.Format("tray.tip.many", count),
        };
        var work = running switch
        {
            0 => "",
            1 => _text["tray.tip.working.one"],
            _ => _text.Format("tray.tip.working.many", running),
        };

        var lines = new[] { news, work }.Where(l => l.Length > 0).ToList();
        _tray.ToolTipText = lines.Count == 0 ? "Meows" : string.Join(Environment.NewLine, lines);
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
        if (_background is not null)
            _background.Changed -= Refresh;
        _tray.IsVisible = false;
        _tray.Dispose();
    }
}
