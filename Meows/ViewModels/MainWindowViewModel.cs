using Meows.Plugins.Abstractions;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Meows.Plugins;
using Meows.Services;
using Meows.Views;

namespace Meows.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly PluginCatalog _catalog;
    private readonly ShellSettings _settings;
    private readonly ShellLog _log;
    private readonly NotificationCenter _notifications;
    private readonly BackgroundTaskService _background;
    private readonly Translations _text;
    private readonly ShellPreferences _preferences;
    private readonly Dictionary<string, TabViewModel> _pluginTabs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _sourceById = new(StringComparer.OrdinalIgnoreCase);
    private readonly MeowsStore? _store;
    private HistoryViewModel? _history;
    private SettingsViewModel? _settingsViewModel;
    private TabViewModel? _historyTab;
    private TabViewModel? _settingsTab;

    private TabViewModel? _selectedTab;
    private bool _isLogVisible;
    private bool _isNotificationsOpen;
    private bool _isTasksOpen;

    public MainWindowViewModel(
        PluginCatalog catalog,
        ShellSettings settings,
        ShellLog log,
        NotificationCenter notifications,
        BackgroundTaskService background,
        Translations text,
        ShellPreferences preferences,
        MeowsStore? store = null)
    {
        _store = store;
        _catalog = catalog;
        _settings = settings;
        _log = log;
        _notifications = notifications;
        _background = background;
        _text = text;
        _preferences = preferences;

        RescanCommand = new RelayCommand(Rescan);
        OpenPluginsFolderCommand = new RelayCommand(OpenPluginsFolder, () => _catalog.PluginsDirectories.Count > 0);
        ToggleLogCommand = new RelayCommand(() => IsLogVisible = !IsLogVisible);
        ToggleNotificationsCommand = new RelayCommand(() => IsNotificationsOpen = !IsNotificationsOpen);
        ToggleTasksCommand = new RelayCommand(() => IsTasksOpen = !IsTasksOpen);
        DismissNotificationCommand = new RelayCommand(DismissNotification);
        InvokeNotificationActionCommand = new RelayCommand(InvokeNotificationAction);
        ClearNotificationsCommand = new RelayCommand(() => _notifications.DismissAllEvents());
        CancelTaskCommand = new RelayCommand(CancelTask);

        _notifications.Changed += RaiseNotificationState;
        _background.Changed += RaiseTaskState;
        _text.PropertyChanged += (_, _) => Retranslate();

        PluginNames.Feline = preferences.FelineNames;
        PluginNames.Changed += OnNamesChanged;

        Palette = new CommandPaletteViewModel(PaletteItems, PaletteSearch);
        OpenPaletteCommand = new RelayCommand(Palette.Open);
    }

    /// <summary>Ctrl+K. Everything the window can do, from one box.</summary>
    public CommandPaletteViewModel Palette { get; }

    public RelayCommand OpenPaletteCommand { get; }

    /// <summary>
    /// What the palette offers before anything is typed: every plugin under both its names,
    /// the shell's own tabs, and the settings that are one word to flip. Built fresh each time
    /// the palette opens, so it always reflects what is installed and what is on.
    /// </summary>
    private IEnumerable<PaletteItem> PaletteItems()
    {
        var text = MeowsText.Current;

        foreach (var entry in Plugins.Where(p => p.IsCompatible))
        {
            var captured = entry;
            var active = _pluginTabs.ContainsKey(entry.Id);
            var title = text.Format(active ? "palette.goto" : "palette.open", entry.DisplayName);
            var other = entry.HasOtherName ? entry.OtherName + " · " : "";
            yield return new PaletteItem(entry.Icon, title, other + entry.Description, () => OpenPlugin(captured), weight: active ? 3 : 2);
        }

        yield return new PaletteItem("⛭", text.Format("palette.goto", text["shell.tab.plugins"]), "", () => SelectedTab = Tabs[0], weight: 1);
        if (_settingsTab is { } settings)
            yield return new PaletteItem("⚙", text.Format("palette.goto", text["shell.tab.settings"]), "", () => SelectedTab = settings, weight: 1);
        if (_historyTab is { } history)
            yield return new PaletteItem("≡", text.Format("palette.goto", text["shell.tab.history"]), "", () => SelectedTab = history, weight: 1);

        if (_settingsViewModel is { } sv)
        {
            yield return new PaletteItem("◐", text["palette.theme.dark"], text["settings.theme"], () => sv.SetTheme(Appearance.Dark));
            yield return new PaletteItem("◑", text["palette.theme.light"], text["settings.theme"], () => sv.SetTheme(Appearance.Light));
            yield return new PaletteItem("◎", text["palette.theme.system"], text["settings.theme"], () => sv.SetTheme(Appearance.System));
            yield return new PaletteItem("Aa", text["palette.language.en"], text["settings.language"], () => sv.SetLanguage("en"));
            yield return new PaletteItem("Aa", text["palette.language.de"], text["settings.language"], () => sv.SetLanguage("de"));
            yield return new PaletteItem("Aa", text["palette.language.system"], text["settings.language"], () => sv.SetLanguage("system"));
        }

        yield return new PaletteItem("🐾", text[PluginNames.Feline ? "palette.names.plain" : "palette.names.feline"], text["settings.names"],
            () => PluginNames.Feline = !PluginNames.Feline);
        yield return new PaletteItem("🔔", text["palette.notifications"], "", () => IsNotificationsOpen = !IsNotificationsOpen);
        yield return new PaletteItem("⏳", text["palette.tasks"], "", () => IsTasksOpen = !IsTasksOpen);
        yield return new PaletteItem("≣", text["palette.log"], "", () => IsLogVisible = !IsLogVisible);
    }

    /// <summary>
    /// What was typed, looked for inside every open plugin and then in the history. A plugin's
    /// own hits come first: a file in Kibble's grid is more likely what is wanted than the line
    /// saying it was queued last week. Each hit brings that tab to the front and lets the plugin
    /// put the thing on screen.
    /// </summary>
    private IEnumerable<PaletteItem> PaletteSearch(string query)
    {
        foreach (var (id, tab) in _pluginTabs)
        {
            var searchable = (tab.Content as Avalonia.Controls.Control)?.DataContext as ISearchable
                             ?? tab.Content as ISearchable;
            if (searchable is null)
                continue;

            var entry = Plugins.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            var name = entry?.DisplayName ?? PluginName(id);
            var glyph = entry?.Icon ?? "•";

            IReadOnlyList<SearchHit> hits;
            try
            {
                hits = searchable.Search(query, 6);
            }
            catch (Exception ex)
            {
                _log.Write("shell", $"'{name}' failed to search: {ex.Message}");
                continue;
            }

            foreach (var hit in hits)
            {
                var open = hit.Open;
                yield return new PaletteItem(glyph, hit.Title, hit.Detail.Length > 0 ? $"{name} · {hit.Detail}" : name, () =>
                {
                    SelectedTab = tab;
                    try
                    {
                        open();
                    }
                    catch (Exception ex)
                    {
                        _log.Write("shell", $"'{name}' failed to open a search hit: {ex.Message}");
                    }
                });
            }
        }

        if (_store is null)
            yield break;

        foreach (var line in _store.Events(null, null, query, 8))
        {
            var subject = line.Subject;
            var name = Path.GetFileName(subject) is { Length: > 0 } file ? file : subject;
            yield return new PaletteItem("≡", name, $"{PluginName(line.Plugin)} · {line.Kind} · {line.At:ddd HH:mm}", () =>
            {
                try
                {
                    if (File.Exists(subject))
                        Explorer.Reveal(subject);
                    else if (Directory.Exists(subject))
                        Explorer.Open(subject);
                }
                catch (Exception)
                {
                }
            }, weight: -1);
        }
    }

    private void OpenPlugin(PluginEntryViewModel entry)
    {
        if (!_pluginTabs.ContainsKey(entry.Id))
        {
            Activate(entry);
            if (!_pluginTabs.ContainsKey(entry.Id))
                return;
            entry.SetActivatedSilently(true);
            PersistActivations();
        }

        SelectedTab = _pluginTabs[entry.Id];
    }

    /// <summary>
    /// Feline or plain names, everywhere at once. Bound from the Settings tab and the bottom bar
    /// alike; the static switch is the single source and this is its handle.
    /// </summary>
    public bool FelineNames
    {
        get => PluginNames.Feline;
        set => PluginNames.Feline = value;
    }

    private void OnNamesChanged()
    {
        _preferences.FelineNames = PluginNames.Feline;
        _settings.SavePreferences(_preferences);
        OnPropertyChanged(nameof(FelineNames));
        Retranslate();
    }

    /// <summary>
    /// Everything the shell holds that is a string rather than a binding to one.
    ///
    /// A view reading {m:Tr key} looks after itself, because that is a binding to the string
    /// table and the table says when it changed. What needs a nudge is anything the shell put
    /// together in code: the tab headers, the plugin cards, and the headings the cards sit under,
    /// which also reorder because they sort by what they say.
    /// </summary>
    private void Retranslate()
    {
        _history?.Retranslate();
        foreach (var entry in Plugins)
            entry.Rename();
        Regroup();
        foreach (var tab in Tabs)
            tab.Retranslate();

        foreach (var entry in Plugins)
            entry.Retranslate();

        Regroup();

        OnPropertyChanged(nameof(ContractVersionText));
        OnPropertyChanged(nameof(PluginsDirectoryText));
        RaiseNotificationState();
        RaiseTaskState();
    }

    public ObservableCollection<TabViewModel> Tabs { get; } = new();

    public ObservableCollection<PluginEntryViewModel> Plugins { get; } = new();

    /// <summary>The same plugins under their headings, which is what the tab actually shows.</summary>
    public ObservableCollection<PluginGroupViewModel> PluginGroups { get; } = new();

    public ObservableCollection<string> LogLines => _log.Lines;

    public ObservableCollection<NotificationItem> Notifications => _notifications.Items;

    public ObservableCollection<BackgroundTaskItem> RunningTasks => _background.Running;

    public RelayCommand RescanCommand { get; }

    public RelayCommand OpenPluginsFolderCommand { get; }

    public RelayCommand ToggleLogCommand { get; }

    public RelayCommand ToggleNotificationsCommand { get; }

    public RelayCommand ToggleTasksCommand { get; }

    public RelayCommand DismissNotificationCommand { get; }

    public RelayCommand InvokeNotificationActionCommand { get; }

    public RelayCommand ClearNotificationsCommand { get; }

    public RelayCommand CancelTaskCommand { get; }

    public TabViewModel? SelectedTab
    {
        get => _selectedTab;
        set => SetField(ref _selectedTab, value);
    }

    public bool IsLogVisible
    {
        get => _isLogVisible;
        set => SetField(ref _isLogVisible, value);
    }

    public bool IsNotificationsOpen
    {
        get => _isNotificationsOpen;
        set
        {
            if (SetField(ref _isNotificationsOpen, value) && value)
                IsTasksOpen = false;
        }
    }

    public bool IsTasksOpen
    {
        get => _isTasksOpen;
        set
        {
            if (SetField(ref _isTasksOpen, value) && value)
                IsNotificationsOpen = false;
        }
    }

    public bool HasNotifications => _notifications.HasAny;

    /// <summary>Status bar badge. The glyph carries the severity so you can read it at a glance.</summary>
    public string NotificationBadge => _notifications.Count == 0
        ? _text["shell.alerts"]
        : $"{Glyph(_notifications.Worst)} {_notifications.Count}";

    public bool HasRunningTasks => _background.RunningCount > 0;

    public string TaskBadge => _background.RunningCount == 0
        ? _text["shell.tasks"]
        : _text.Format("shell.tasks.count", _background.RunningCount);

    private static string Glyph(NotificationSeverity? severity) => severity switch
    {
        NotificationSeverity.Error => "⛔",
        NotificationSeverity.Warning => "⚠",
        _ => "ℹ",
    };

    private void RaiseNotificationState()
    {
        OnPropertyChanged(nameof(HasNotifications));
        OnPropertyChanged(nameof(NotificationBadge));
    }

    private void RaiseTaskState()
    {
        OnPropertyChanged(nameof(HasRunningTasks));
        OnPropertyChanged(nameof(TaskBadge));
    }

    private void DismissNotification(object? parameter)
    {
        if (parameter is NotificationItem item)
            _notifications.Dismiss(item);
    }

    private void InvokeNotificationAction(object? parameter)
    {
        if (parameter is not NotificationItem { Action: not null } item)
            return;
        try
        {
            item.Action.Invoke();
        }
        catch (Exception ex)
        {
            // It is plugin code. Do not let it take the shell down.
            _log.Write("shell", $"Notification action '{item.ActionLabel}' threw: {ex}");
        }
    }

    private void CancelTask(object? parameter)
    {
        if (parameter is BackgroundTaskItem task)
            task.Cancel();
    }

    /// <summary>Version off the assembly, so the status bar always names the running build.</summary>
    public static string AppVersionText
    {
        get
        {
            var v = typeof(MainWindowViewModel).Assembly.GetName().Version;
            return v is null ? "Meows" : $"Meows {v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
        }
    }

    public string ContractVersionText =>
        _text.Format("plugins.contract", ContractCompatibility.ShellVersionText);

    public bool HasIncompatiblePlugins => Plugins.Any(p => p.IsIncompatible);

    public string PluginsDirectoryText =>
        _catalog.PluginsDirectory ?? _text["plugins.nodirectory"];

    public bool HasPlugins => Plugins.Count > 0;

    public void Initialize()
    {
        // Always tab zero, so an empty plugins folder still opens on something useful.
        Tabs.Add(new TabViewModel("shell.tab.plugins", "⛭", new PluginsView { DataContext = this }));
        _settingsViewModel = new SettingsViewModel(_settings, _text, _log, _preferences);
        _settingsTab = new TabViewModel("shell.tab.settings", "⚙", new SettingsView { DataContext = _settingsViewModel });
        Tabs.Add(_settingsTab);
        if (_store is not null)
        {
            _history = new HistoryViewModel(_store, PluginName, UndoTargetFor, ShowPlugin);
            _historyTab = new TabViewModel("shell.tab.history", "≡", new HistoryView { DataContext = _history });
            Tabs.Add(_historyTab);
        }
        SelectedTab = Tabs[0];
        Rescan();
    }

    private void Rescan()
    {
        foreach (var entry in Plugins.Where(p => p.IsActivated).ToList())
            Deactivate(entry);

        Plugins.Clear();

        var activated = _settings.LoadActivatedPlugins();
        foreach (var descriptor in _catalog.Discover())
        {
            // Its strings before its card, not when it is switched on. A card carries the
            // plugin's own description, so waiting for activation left every inactive plugin
            // introducing itself with a dotted key.
            if (descriptor.Plugin is { } plugin)
                _text.Add(plugin.GetType().Assembly);

            Plugins.Add(new PluginEntryViewModel(descriptor, OnActivationChanged));
        }

        Regroup();

        OnPropertyChanged(nameof(PluginsDirectoryText));
        OnPropertyChanged(nameof(HasPlugins));
        OpenPluginsFolderCommand.RaiseCanExecuteChanged();

        foreach (var entry in Plugins.Where(p => activated.Contains(p.Id)))
            entry.IsActivated = true;
    }

    private void Regroup()
    {
        PluginGroups.Clear();
        foreach (var group in PluginGroupViewModel.Arrange(Plugins))
            PluginGroups.Add(group);
    }

    /// <summary>
    /// Opens the folders plugins are read from. Usually one, but MEOWS_PLUGINS_DIR adds to the
    /// search rather than replacing it, so there can be more than one and opening only the first
    /// would hide the one the user actually went looking for.
    /// </summary>
    private void OpenPluginsFolder()
    {
        foreach (var directory in _catalog.PluginsDirectories.Where(Directory.Exists))
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _log.Write("shell", $"Could not open {directory}: {ex.Message}");
            }
        }
    }

    private void OnActivationChanged(PluginEntryViewModel entry, bool activated)
    {
        if (activated)
            Activate(entry);
        else
            Deactivate(entry);

        PersistActivations();
    }

    private void Activate(PluginEntryViewModel entry)
    {
        if (_pluginTabs.ContainsKey(entry.Id))
            return;

        if (!entry.IsCompatible || entry.Descriptor.Plugin is null)
        {
            entry.SetActivatedSilently(false);
            _log.Write("shell", $"Refused to activate '{entry.DisplayName}': {entry.IncompatibleReason}");
            return;
        }

        try
        {
            var host = new PluginHost(entry.Id, entry.DisplayName, _settings, _log, _notifications, _background,
                new HandoffService(entry.Id, CanReach, SendHandoff), _store?.For(entry.Id));
            _sourceById[entry.Id] = entry.DisplayName;
            var view = entry.Descriptor.Plugin!.CreateView(host);
            var tab = new TabViewModel(() => entry.DisplayName, entry.Icon, view);
            _pluginTabs[entry.Id] = tab;
            Tabs.Add(tab);
            SelectedTab = tab;
            entry.Error = null;
            _log.Write("shell", $"Activated '{entry.DisplayName}'.");
        }
        catch (Exception ex)
        {
            // Mark it failed and carry on. One bad plugin should not close the window.
            entry.Error = Explain(ex);
            entry.SetActivatedSilently(false);
            _log.Write("shell", $"'{entry.DisplayName}' failed to activate: {ex}");
        }
    }

    /// <summary>
    /// Turns a load failure into something a person can act on.
    ///
    /// A plugin that cannot find one of its own libraries is nearly always a half unpacked
    /// download: running the exe straight out of a zip makes the archive tool extract the exe
    /// and miss files buried in the plugin folders. "FileNotFoundException: Meows.Disk" is true
    /// and tells the reader nothing, so say what it actually means.
    /// </summary>
    private static string Explain(Exception ex)
    {
        if (ex is not FileNotFoundException missing)
            return ex.Message;

        var name = missing.FileName is { Length: > 0 } full
            ? full.Split(',')[0]
            : "a file";

        return MeowsText.Current.Format("plugins.missingfile", name);
    }

    /// <summary>The open plugin's view model, if it reverses its own history lines. Not opened for the asking.</summary>
    private IUndoTarget? UndoTargetFor(string pluginId)
    {
        if (!_pluginTabs.TryGetValue(pluginId, out var tab))
            return null;
        return (tab.Content as Avalonia.Controls.Control)?.DataContext as IUndoTarget ?? tab.Content as IUndoTarget;
    }

    private void ShowPlugin(string pluginId)
    {
        if (_pluginTabs.TryGetValue(pluginId, out var tab))
            SelectedTab = tab;
    }

    /// <summary>A plugin's name for its id, for the History tab; the id itself when it is not installed any more.</summary>
    private string PluginName(string pluginId) =>
        Plugins.FirstOrDefault(p => string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? pluginId;

    /// <summary>Installed and loadable. Whether it takes a particular handoff is only known once it is open.</summary>
    private bool CanReach(string pluginId) =>
        Plugins.Any(p => string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase) && p.IsCompatible);

    /// <summary>
    /// One plugin handing work to another. The receiver is switched on if it is not already,
    /// because "find duplicates here" from Chonk means open Purrge; its tab comes to the front;
    /// and its view model is asked whether it takes this before it is given it.
    /// </summary>
    private bool SendHandoff(string fromId, string toId, Handoff handoff)
    {
        var entry = Plugins.FirstOrDefault(p => string.Equals(p.Id, toId, StringComparison.OrdinalIgnoreCase));
        if (entry is null || !entry.IsCompatible)
            return false;

        if (!_pluginTabs.ContainsKey(entry.Id))
        {
            Activate(entry);
            if (!_pluginTabs.ContainsKey(entry.Id))
                return false;
            entry.SetActivatedSilently(true);
            PersistActivations();
        }

        var tab = _pluginTabs[entry.Id];
        var target = (tab.Content as Avalonia.Controls.Control)?.DataContext as IHandoffTarget
                     ?? tab.Content as IHandoffTarget;

        if (target is null || !target.Accepts(handoff))
        {
            _log.Write("shell", $"'{entry.DisplayName}' does not take a {handoff.Verb} handoff from {fromId}.");
            return false;
        }

        SelectedTab = tab;
        try
        {
            target.Receive(handoff);
            _log.Write("shell", $"{fromId} handed {handoff.Paths.Count} path(s) to '{entry.DisplayName}'.");
            return true;
        }
        catch (Exception ex)
        {
            _log.Write("shell", $"'{entry.DisplayName}' failed to take a handoff: {ex}");
            return false;
        }
    }

    private void Deactivate(PluginEntryViewModel entry)
    {
        // Order matters. Stop its work and take down its notifications before the view goes,
        // or a cancelled task can post into a shell that has forgotten the plugin.
        _background.CancelAllFor(entry.Id);
        if (_sourceById.TryGetValue(entry.Id, out var source))
            _notifications.RemoveAllFrom(source);

        if (!_pluginTabs.Remove(entry.Id, out var tab))
            return;

        Tabs.Remove(tab);
        (tab.Content as IDisposable)?.Dispose();
        if ((tab.Content as Avalonia.StyledElement)?.DataContext is IDisposable disposableContext)
            disposableContext.Dispose();

        SelectedTab ??= Tabs.FirstOrDefault();
        _log.Write("shell", $"Deactivated '{entry.DisplayName}'.");
    }

    private void PersistActivations() =>
        _settings.SaveActivatedPlugins(Plugins.Where(p => p.IsActivated).Select(p => p.Id));

    public void Shutdown()
    {
        foreach (var entry in Plugins.Where(p => p.IsActivated).ToList())
            Deactivate(entry);
        _background.Dispose();
    }
}
