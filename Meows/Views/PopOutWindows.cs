using Avalonia.Controls;
using Avalonia.Threading;
using Meows.Services;
using Meows.ViewModels;

namespace Meows.Views;

/// <summary>
/// Any tab into a window of its own, and back. The map on the television, the run sheet on
/// the laptop, without a second application. Where each window sat is remembered per monitor
/// layout, so the tab that lived on the second screen goes back there when that screen is
/// plugged in and lands somewhere sensible when it is not; and which tabs were out is
/// remembered too, so a restart puts them back out.
///
/// A control can be in one visual tree at a time, so the tab shows a notice first and the
/// window takes the content on the next pass, when the tab has let go; the same in reverse.
/// </summary>
public sealed class PopOutWindows
{
    private readonly Func<Window?> _mainWindow;
    private readonly ShellPreferences _preferences;
    private readonly Action _savePreferences;
    private readonly ShellLog _log;
    private readonly Dictionary<TabViewModel, Window> _open = [];

    public PopOutWindows(Func<Window?> mainWindow, ShellPreferences preferences, Action savePreferences, ShellLog log)
    {
        _mainWindow = mainWindow;
        _preferences = preferences;
        _savePreferences = savePreferences;
        _log = log;
    }

    public bool IsOut(TabViewModel tab) => _open.ContainsKey(tab);

    /// <summary>The tabs that were out when Meows last quit, by key.</summary>
    public IReadOnlyList<string> RememberedOut => _preferences.PoppedOutTabs;

    public void PopOut(TabViewModel tab)
    {
        if (_open.ContainsKey(tab) || tab.Content is not Control control)
            return;

        var main = _mainWindow();
        var window = new Window
        {
            Title = $"{tab.Header} · Meows",
            Icon = main?.Icon,
            MinWidth = 480,
            MinHeight = 320,
        };

        var layout = WindowLayout.Of(main);
        if (layout is not null && Places(layout).TryGetValue(tab.Key, out var place))
            WindowLayout.Apply(window, place);
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.Width = 1000;
            window.Height = 700;
        }

        window.Closing += (_, _) =>
        {
            Remember(tab, window);
            window.Content = null;
            _open.Remove(tab);
            // The tab takes its content back once the window has let go of it.
            Dispatcher.UIThread.Post(() => tab.IsPoppedOut = false);
            RememberOut();
        };

        _open[tab] = window;
        tab.IsPoppedOut = true;
        RememberOut();

        // After the tab's ContentControl has dropped the control, not before.
        Dispatcher.UIThread.Post(() =>
        {
            if (!_open.ContainsKey(tab))
                return;
            if (control.Parent is not null)
            {
                // Still held; one more turn is usually enough, and never taking it is the safe failure.
                Dispatcher.UIThread.Post(() => Attach(tab, window, control), DispatcherPriority.Background);
                return;
            }
            Attach(tab, window, control);
        }, DispatcherPriority.Background);
    }

    private void Attach(TabViewModel tab, Window window, Control control)
    {
        if (!_open.ContainsKey(tab))
            return;
        if (control.Parent is not null)
        {
            _log.Write("shell", $"Could not move '{tab.Header}' into its own window: the tab still holds it.");
            _open.Remove(tab);
            tab.IsPoppedOut = false;
            RememberOut();
            return;
        }
        window.Content = control;
        window.Show();
    }

    public void BringBack(TabViewModel tab)
    {
        if (_open.TryGetValue(tab, out var window))
            window.Close();
    }

    /// <summary>Called when a tab goes away for good: its window goes with it, and it is not remembered as out.</summary>
    public void Forget(TabViewModel tab)
    {
        if (_open.TryGetValue(tab, out var window))
        {
            Remember(tab, window);
            window.Content = null;
            _open.Remove(tab);
            window.Close();
        }
        _preferences.PoppedOutTabs.Remove(tab.Key);
        _savePreferences();
    }

    /// <summary>At quit: every window's place, and which were out, before the windows go.</summary>
    public void CloseAll()
    {
        var out_ = _open.Keys.Select(t => t.Key).ToList();
        foreach (var (tab, window) in _open.ToList())
        {
            Remember(tab, window);
            window.Content = null;
            window.Close();
        }
        _open.Clear();
        _preferences.PoppedOutTabs = out_;
        _savePreferences();
    }

    private void RememberOut()
    {
        _preferences.PoppedOutTabs = _open.Keys.Select(t => t.Key).ToList();
        _savePreferences();
    }

    private void Remember(TabViewModel tab, Window window)
    {
        var layout = WindowLayout.Of(window);
        if (layout is null || WindowLayout.PlaceOf(window) is not { } place)
            return;
        var places = Places(layout);
        places[tab.Key] = place;
        _preferences.PopOutPlaces[layout] = places;
        _savePreferences();
    }

    private Dictionary<string, WindowPlace> Places(string layout) =>
        _preferences.PopOutPlaces.TryGetValue(layout, out var places) ? places : [];
}
