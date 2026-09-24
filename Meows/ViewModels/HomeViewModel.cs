using System.Collections.ObjectModel;
using Meows.Plugins.Abstractions;
using Meows.Services;

namespace Meows.ViewModels;

/// <summary>
/// One plugin's line on the Home tab: its name, what it says about itself if it says anything,
/// what it last did, and a way in.
/// </summary>
public sealed class HomePluginLine(PluginEntryViewModel entry, Glance? glance, Action<PluginEntryViewModel> open)
{
    public PluginEntryViewModel Entry { get; } = entry;

    public string Icon => Entry.Icon;

    public string Name => Entry.DisplayName;

    /// <summary>
    /// The plugin's own line, through <see cref="IGlanceable"/>, when it has one. It stands
    /// above the shell's line rather than in place of it: "2 queues would fail tonight" and
    /// "shrank IMG_0412.jpg, 3 hours ago" are both worth having.
    /// </summary>
    public string GlanceText => glance?.Text ?? "";

    public bool HasGlance => glance is { Text.Length: > 0 };

    public string Health => Entry.HasHealth ? Entry.HealthText : MeowsText.Current["home.plugin.quiet"];

    public bool IsTrouble => Entry.HealthIsTrouble;

    public bool GlanceIsTrouble => glance is { IsTrouble: true };

    public RelayCommand OpenCommand { get; } = new(() => open(entry));
}

/// <summary>
/// Tab zero: what happened while the window was away. Meows lives in the tray, so the window
/// is opened after hours rather than watched, and the first thing it shows should be the
/// answer to "anything?": the notifications that are up, what is running and watching, each
/// switched-on plugin's last line, and the history since the window was last hidden. All of it
/// exists elsewhere in the shell; this is the one page that has it together.
/// </summary>
public sealed class HomeViewModel : ObservableObject, IDisposable
{
    private readonly ObservableCollection<PluginEntryViewModel> _plugins;
    private readonly Func<PluginEntryViewModel, bool> _isOn;
    private readonly Func<PluginEntryViewModel, Glance?> _glance;
    private readonly Action<PluginEntryViewModel> _open;
    private readonly MeowsStore? _store;
    private readonly Func<string, string> _pluginName;
    private readonly Func<DateTime?> _lastSeen;
    private readonly BackgroundTaskService _background;
    private readonly NotificationCenter _notifications;

    public HomeViewModel(
        ObservableCollection<PluginEntryViewModel> plugins,
        Func<PluginEntryViewModel, bool> isOn,
        Func<PluginEntryViewModel, Glance?> glance,
        Action<PluginEntryViewModel> open,
        NotificationCenter notifications,
        BackgroundTaskService background,
        MeowsStore? store,
        Func<string, string> pluginName,
        Func<DateTime?> lastSeen,
        Action<object?> invokeNotification)
    {
        InvokeNotificationCommand = new RelayCommand(invokeNotification);
        RevealCommand = new RelayCommand(Reveal);
        _plugins = plugins;
        _isOn = isOn;
        _glance = glance;
        _open = open;
        _notifications = notifications;
        _background = background;
        _store = store;
        _pluginName = pluginName;
        _lastSeen = lastSeen;

        _notifications.Changed += Refresh;
        _background.Changed += Refresh;
        _background.WatchesChanged += Refresh;
        if (_store is not null)
            _store.Recorded += OnRecorded;
        RefreshCommand = new RelayCommand(Refresh);
    }

    public ObservableCollection<NotificationItem> Notifications => _notifications.Items;

    public bool HasNotifications => _notifications.HasAny;

    public ObservableCollection<BackgroundTaskItem> Running => _background.Running;

    public bool HasRunning => _background.RunningCount > 0;

    public ObservableCollection<HomePluginLine> Plugins { get; } = [];

    public bool HasPlugins => Plugins.Count > 0;

    public ObservableCollection<HistoryLineViewModel> Recent { get; } = [];

    /// <summary>The last seven days, counted from the history: the weekly recap.</summary>
    public ObservableCollection<string> Week { get; } = [];

    public bool HasWeek => Week.Count > 0;

    private DateTime _weekCountedAt = DateTime.MinValue;

    /// <summary>
    /// Counted again at most every five minutes: Home refreshes on every notification and every
    /// task, and a week of history is more than the dozen lines the rest of the page asks for.
    /// </summary>
    private void CountWeek(bool force)
    {
        if (_store is null || (!force && DateTime.UtcNow - _weekCountedAt < TimeSpan.FromMinutes(5)))
            return;
        _weekCountedAt = DateTime.UtcNow;
        var now = DateTime.UtcNow;
        var recap = WeeklyRecap.Of(_store.Between(now - WeeklyRecap.Week, now), now - WeeklyRecap.Week, now);
        Week.Clear();
        foreach (var line in WeeklyRecap.Lines(recap, _pluginName, KindLabel, MeowsText.Current))
            Week.Add(line);
        OnPropertyChanged(nameof(HasWeek));
    }

    /// <summary>The words a plugin gave the Rules tab for one kind of line, when it gave any.</summary>
    private string? KindLabel(string plugin, string kind)
    {
        var entry = _plugins.FirstOrDefault(p => string.Equals(p.Id, plugin, StringComparison.OrdinalIgnoreCase));
        try
        {
            return entry?.Descriptor.Plugin?.Records.FirstOrDefault(r => r.Kind == kind)?.Label;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public bool HasRecent => Recent.Count > 0;

    public RelayCommand RefreshCommand { get; }

    /// <summary>The window's own handler, so a button here does what the same button in the panel does.</summary>
    public RelayCommand InvokeNotificationCommand { get; }

    public RelayCommand RevealCommand { get; }

    private static void Reveal(object? parameter)
    {
        if (parameter is not HistoryLineViewModel { IsPath: true } line)
            return;
        try
        {
            if (File.Exists(line.Subject))
                Explorer.Reveal(line.Subject);
            else if (Directory.Exists(line.Subject))
                Explorer.Open(line.Subject);
        }
        catch (Exception)
        {
            // Gone since it was recorded; the line still says what happened.
        }
    }

    /// <summary>"Since Tuesday 21:40", or the plain heading when the window has never been hidden.</summary>
    public string SinceText
    {
        get
        {
            var text = MeowsText.Current;
            return _lastSeen() is { } seen
                ? text.Format("home.since", seen.Date == DateTime.Today ? seen.ToString("HH:mm") : seen.ToString("ddd HH:mm"))
                : text["home.since.never"];
        }
    }

    /// <summary>The schedules, in one line: how many are watching and how many have stopped.</summary>
    public string WatchesText
    {
        get
        {
            var watches = _background.Watches();
            var text = MeowsText.Current;
            if (watches.Count == 0)
                return text["home.watches.none"];
            var stopped = watches.Count(w => w.IsStopped);
            return stopped == 0
                ? text.Format("home.watches", watches.Count)
                : text.Format("home.watches.stopped", watches.Count, stopped);
        }
    }

    public bool WatchesInTrouble => _background.Watches().Any(w => w.IsStopped);

    /// <summary>Everything again. Cheap: the store is asked for a dozen lines and the rest is in memory.</summary>
    public void Refresh()
    {
        Plugins.Clear();
        foreach (var entry in _plugins.Where(_isOn))
            Plugins.Add(new HomePluginLine(entry, _glance(entry), _open));

        Recent.Clear();
        if (_store is not null)
        {
            var since = _lastSeen();
            var lines = _store.Events(null, null, null, 60)
                .Where(e => since is null || e.At > since)
                .Take(12);
            foreach (var stored in lines)
                Recent.Add(new HistoryLineViewModel(stored, _pluginName(stored.Plugin)));
        }

        CountWeek(force: false);

        OnPropertyChanged(nameof(HasNotifications));
        OnPropertyChanged(nameof(HasRunning));
        OnPropertyChanged(nameof(HasPlugins));
        OnPropertyChanged(nameof(HasRecent));
        OnPropertyChanged(nameof(SinceText));
        OnPropertyChanged(nameof(WatchesText));
        OnPropertyChanged(nameof(WatchesInTrouble));
    }

    public void Retranslate()
    {
        _weekCountedAt = DateTime.MinValue;
        Refresh();
    }

    private void OnRecorded(string pluginId) => Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);

    public void Dispose()
    {
        _notifications.Changed -= Refresh;
        _background.Changed -= Refresh;
        _background.WatchesChanged -= Refresh;
        if (_store is not null)
            _store.Recorded -= OnRecorded;
    }
}
