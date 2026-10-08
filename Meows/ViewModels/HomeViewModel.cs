using System.Collections.ObjectModel;
using Meows.Plugins.Abstractions;
using Meows.Services;

namespace Meows.ViewModels;

/// <summary>
/// One plugin's line on the Home tab: its name, what it says about itself if it says anything,
/// what it last did, and a way in. Rebuilt on every refresh, so the move and hide buttons read
/// fresh rather than notifying.
/// </summary>
public sealed class HomePluginLine
{
    public HomePluginLine(
        PluginEntryViewModel entry,
        IReadOnlyList<Glance> glances,
        Action<PluginEntryViewModel> open,
        Action<PluginEntryViewModel> moveUp,
        Action<PluginEntryViewModel> moveDown,
        Action<PluginEntryViewModel> hide,
        bool canMoveUp,
        bool canMoveDown)
    {
        Entry = entry;
        Glances = glances;
        OpenCommand = new RelayCommand(() => open(entry));
        MoveUpCommand = new RelayCommand(() => moveUp(entry));
        MoveDownCommand = new RelayCommand(() => moveDown(entry));
        HideCommand = new RelayCommand(() => hide(entry));
        CanMoveUp = canMoveUp;
        CanMoveDown = canMoveDown;
    }

    public PluginEntryViewModel Entry { get; }

    public string Icon => Entry.Icon;

    public string Name => Entry.DisplayName;

    /// <summary>Up to three lines from the plugin, most telling first. Empty when it says nothing.</summary>
    public IReadOnlyList<Glance> Glances { get; }

    /// <summary>
    /// The plugin's own first line, when it has one. It stands above the shell's line rather
    /// than in place of it: "2 queues would fail tonight" and "shrank IMG_0412.jpg, 3 hours
    /// ago" are both worth having.
    /// </summary>
    public string GlanceText => Glances.Count > 0 ? Glances[0].Text : "";

    public bool HasGlance => Glances.Count > 0 && Glances[0].Text.Length > 0;

    /// <summary>The rest of what a multi-line plugin says, after the first line.</summary>
    public IReadOnlyList<Glance> ExtraGlances => Glances.Count > 1 ? Glances.Skip(1).ToList() : [];

    public string Health => Entry.HasHealth ? Entry.HealthText : MeowsText.Current["home.plugin.quiet"];

    public bool IsTrouble => Entry.HealthIsTrouble;

    public bool GlanceIsTrouble => Glances.Count > 0 && Glances[0].IsTrouble;

    public RelayCommand OpenCommand { get; }

    public RelayCommand MoveUpCommand { get; }

    public RelayCommand MoveDownCommand { get; }

    public RelayCommand HideCommand { get; }

    public bool CanMoveUp { get; }

    public bool CanMoveDown { get; }
}

/// <summary>A hidden card, listed so it can come back. Nothing here runs; it only names.</summary>
public sealed class HomeHiddenLine
{
    public HomeHiddenLine(PluginEntryViewModel entry, Action<PluginEntryViewModel> show)
    {
        Entry = entry;
        ShowCommand = new RelayCommand(() => show(entry));
    }

    public PluginEntryViewModel Entry { get; }

    public string Icon => Entry.Icon;

    public string Name => Entry.DisplayName;

    public RelayCommand ShowCommand { get; }
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
    private readonly Func<PluginEntryViewModel, IReadOnlyList<Glance>?>? _glances;
    private readonly Action<PluginEntryViewModel> _open;
    private readonly MeowsStore? _store;
    private readonly Func<string, string> _pluginName;
    private readonly Func<DateTime?> _lastSeen;
    private readonly BackgroundTaskService _background;
    private readonly NotificationCenter _notifications;
    private readonly IList<string> _homeOrder;
    private readonly IList<string> _homeHidden;
    private readonly Action? _saveHome;
    private readonly ShellPicker? _picker;

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
        Action<object?> invokeNotification,
        Func<PluginEntryViewModel, IReadOnlyList<Glance>?>? glances = null,
        IList<string>? homeOrder = null,
        IList<string>? homeHidden = null,
        Action? saveHome = null,
        ShellPicker? picker = null)
    {
        InvokeNotificationCommand = new RelayCommand(invokeNotification);
        RevealCommand = new RelayCommand(Reveal);
        _plugins = plugins;
        _isOn = isOn;
        _glance = glance;
        _glances = glances;
        _open = open;
        _notifications = notifications;
        _background = background;
        _store = store;
        _pluginName = pluginName;
        _lastSeen = lastSeen;
        _homeOrder = homeOrder ?? [];
        _homeHidden = homeHidden ?? [];
        _saveHome = saveHome;
        _picker = picker;

        ResetOrderCommand = new RelayCommand(ResetOrder);
        WeekPageCommand = new RelayCommand(() => _ = WeekPageAsync(), () => _picker is not null);

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

    public ObservableCollection<HomeHiddenLine> HiddenPlugins { get; } = [];

    public bool HasHiddenPlugins => HiddenPlugins.Count > 0;

    public ObservableCollection<HistoryLineViewModel> Recent { get; } = [];

    public ObservableCollection<HistoryLineViewModel> Overnight { get; } = [];

    public bool HasOvernight => Overnight.Count > 0;

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

    public RelayCommand ResetOrderCommand { get; }

    public RelayCommand WeekPageCommand { get; }

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
        var on = _plugins.Where(_isOn).ToList();
        var hidden = new HashSet<string>(_homeHidden, StringComparer.OrdinalIgnoreCase);
        var position = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in _homeOrder)
            position.TryAdd(id, position.Count);
        var catalog = on.Select((p, i) => (p, i)).ToDictionary(t => t.p.Id, t => t.i, StringComparer.OrdinalIgnoreCase);

        var visible = on
            .Where(p => !hidden.Contains(p.Id))
            .OrderBy(p => position.TryGetValue(p.Id, out var at) ? at : int.MaxValue)
            .ThenBy(p => catalog.TryGetValue(p.Id, out var at) ? at : int.MaxValue)
            .ToList();

        Plugins.Clear();
        for (var i = 0; i < visible.Count; i++)
        {
            var entry = visible[i];
            Plugins.Add(new HomePluginLine(entry, GlancesOf(entry), _open,
                MoveUp, MoveDown, Hide, i > 0, i < visible.Count - 1));
        }

        HiddenPlugins.Clear();
        foreach (var entry in on.Where(p => hidden.Contains(p.Id)))
            HiddenPlugins.Add(new HomeHiddenLine(entry, Show));

        var since = _lastSeen();
        Recent.Clear();
        Overnight.Clear();
        if (_store is not null)
        {
            var lines = _store.Events(null, null, null, 60)
                .Where(e => since is null || e.At > since)
                .Take(12);
            foreach (var stored in lines)
                Recent.Add(new HistoryLineViewModel(stored, _pluginName(stored.Plugin)));

            // What the rules did while the window was hidden: firings, failures and declines,
            // but not the pauses, which the notifications already say out loud.
            if (since is not null)
            {
                foreach (var stored in _store.Events(InstinctEngine.PluginId, null, null, 60)
                    .Where(e => e.At > since && e.Kind is "fired" or "failed" or "declined")
                    .Take(12))
                    Overnight.Add(new HistoryLineViewModel(stored, _pluginName(stored.Plugin)));
            }
        }

        CountWeek(force: false);

        OnPropertyChanged(nameof(HasNotifications));
        OnPropertyChanged(nameof(HasRunning));
        OnPropertyChanged(nameof(HasPlugins));
        OnPropertyChanged(nameof(HasHiddenPlugins));
        OnPropertyChanged(nameof(HasRecent));
        OnPropertyChanged(nameof(HasOvernight));
        OnPropertyChanged(nameof(SinceText));
        OnPropertyChanged(nameof(WatchesText));
        OnPropertyChanged(nameof(WatchesInTrouble));
    }

    /// <summary>The lines for one card: up to three from a multi-line plugin, else its one line, else none.</summary>
    private IReadOnlyList<Glance> GlancesOf(PluginEntryViewModel entry)
    {
        if (_glances?.Invoke(entry) is { Count: > 0 } multi)
            return multi.Take(3).ToList();
        return _glance(entry) is { } single ? [single] : [];
    }

    /// <summary>The ids on screen now, in the order they are shown.</summary>
    private List<string> VisibleIds() =>
        Plugins.Select(p => p.Entry.Id).ToList();

    private void MoveUp(PluginEntryViewModel entry) => Move(entry, -1);

    private void MoveDown(PluginEntryViewModel entry) => Move(entry, +1);

    private void Move(PluginEntryViewModel entry, int direction)
    {
        var ids = VisibleIds();
        var at = ids.FindIndex(id => string.Equals(id, entry.Id, StringComparison.OrdinalIgnoreCase));
        var to = Math.Clamp(at + direction, 0, ids.Count - 1);
        if (at < 0 || at == to)
            return;

        (ids[at], ids[to]) = (ids[to], ids[at]);
        WriteOrder(ids);
        Refresh();
    }

    private void Hide(PluginEntryViewModel entry)
    {
        if (!_homeHidden.Contains(entry.Id, StringComparer.OrdinalIgnoreCase))
            _homeHidden.Add(entry.Id);
        var hidden = new HashSet<string>(_homeHidden, StringComparer.OrdinalIgnoreCase);
        WriteOrder(VisibleIds().Where(id => !hidden.Contains(id)).ToList());
        Refresh();
    }

    private void Show(PluginEntryViewModel entry)
    {
        for (var i = _homeHidden.Count - 1; i >= 0; i--)
            if (string.Equals(_homeHidden[i], entry.Id, StringComparison.OrdinalIgnoreCase))
                _homeHidden.RemoveAt(i);
        WriteOrder(VisibleIds());
        Refresh();
    }

    private void ResetOrder()
    {
        _homeOrder.Clear();
        _homeHidden.Clear();
        _saveHome?.Invoke();
        Refresh();
    }

    /// <summary>
    /// The week on one page for the fridge door: what is up now, what the week held, and what
    /// happened since the window was last hidden. Saved where pointed, then opened.
    /// </summary>
    private async Task WeekPageAsync()
    {
        if (_picker is null)
            return;

        try
        {
            var picked = await _picker.Save(new PickOptions
            {
                Title = MeowsText.Current["home.page.dialog"],
                SuggestedName = $"family-week-{DateTime.Today:yyyy-MM-dd}.md",
            });
            if (string.IsNullOrWhiteSpace(picked))
                return;

            var page = FamilyPage.Of(
                Plugins.Select(p => new FamilyPage.CardLines(p.Name, p.Glances)).ToList(),
                Week.ToList(),
                Recent.Select(r => (r.Plugin, r.ShortSubject)).ToList(),
                DateTime.Today,
                MeowsText.Current);
            File.WriteAllText(picked, page);
            Explorer.Open(picked);
        }
        catch (Exception ex)
        {
            _notifications.Post("meows.home", NotificationSeverity.Warning,
                MeowsText.Current["home.page"], MeowsText.Current.Format("home.page.failed", ex.Message),
                (NotificationAction?)null);
        }
    }

    /// <summary>
    /// Writes the order down: what is shown in the order shown, then what is hidden in the
    /// order it already had, then anything gone. Only ids somebody arranged are ever in here.
    /// </summary>
    private void WriteOrder(List<string> visibleIds)
    {
        var hidden = _homeOrder
            .Where(id => _homeHidden.Contains(id, StringComparer.OrdinalIgnoreCase) && !visibleIds.Contains(id, StringComparer.OrdinalIgnoreCase))
            .ToList();
        _homeOrder.Clear();
        foreach (var id in visibleIds)
            _homeOrder.Add(id);
        foreach (var id in hidden)
            _homeOrder.Add(id);
        _saveHome?.Invoke();
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
