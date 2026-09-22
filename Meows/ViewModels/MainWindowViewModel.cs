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
    private readonly PluginUpdates _updater;
    private readonly Dictionary<string, ISearchable?> _dormant = new(StringComparer.OrdinalIgnoreCase);
    private AvailableUpdate? _shellUpdate;
    private readonly ShellPicker _picker;
    private readonly PopOutWindows _popOuts;
    private bool _popOutsRestored;
    private readonly Dictionary<string, AvailableUpdate> _availableUpdates = new(StringComparer.OrdinalIgnoreCase);
    private HistoryViewModel? _history;
    private HomeViewModel? _home;
    private TabViewModel? _pluginsTab;
    private SettingsViewModel? _settingsViewModel;
    private TabViewModel? _historyTab;
    private TabViewModel? _settingsTab;

    private TabViewModel? _selectedTab;
    private string? _installNotice;
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
        MeowsStore? store = null,
        PluginUpdates? updater = null)
    {
        _store = store;
        _updater = updater ?? new PluginUpdates(AppVersion);
        _catalog = catalog;
        _settings = settings;
        _log = log;
        _notifications = notifications;
        _background = background;
        _text = text;
        _preferences = preferences;
        _picker = new ShellPicker(() => Window);
        _popOuts = new PopOutWindows(() => Window, _preferences, SavePreferences, _log);
        PopOutCommand = new RelayCommand(p => { if (p is TabViewModel tab) _popOuts.PopOut(tab); });
        BringBackCommand = new RelayCommand(p => { if (p is TabViewModel tab) _popOuts.BringBack(tab); });

        RescanCommand = new RelayCommand(Rescan);
        OpenPluginsFolderCommand = new RelayCommand(OpenPluginsFolder, () => _catalog.PluginsDirectories.Count > 0);
        ToggleLogCommand = new RelayCommand(() => IsLogVisible = !IsLogVisible);
        ToggleNotificationsCommand = new RelayCommand(() => IsNotificationsOpen = !IsNotificationsOpen);
        ToggleTasksCommand = new RelayCommand(() => IsTasksOpen = !IsTasksOpen);
        DismissNotificationCommand = new RelayCommand(DismissNotification);
        InvokeNotificationActionCommand = new RelayCommand(InvokeNotificationAction);
        ClearNotificationsCommand = new RelayCommand(() => _notifications.DismissAllEvents());
        OpenShellUpdateCommand = new RelayCommand(OpenShellUpdate, () => HasShellUpdate);
        CancelTaskCommand = new RelayCommand(CancelTask);

        _notifications.Changed += RaiseNotificationState;
        _background.Changed += RaiseTaskState;
        _background.WatchesChanged += OnWatchesChanged;
        if (_store is not null)
            _store.Recorded += OnRecorded;
        _text.PropertyChanged += (_, _) => Retranslate();

        PluginNames.Feline = preferences.FelineNames;
        PluginNames.Changed += OnNamesChanged;

        Palette = new CommandPaletteViewModel(PaletteItems, PaletteSearch);
        OpenPaletteCommand = new RelayCommand(Palette.Open);
        SelectTabCommand = new RelayCommand(tab =>
        {
            if (tab is TabViewModel picked)
                SelectedTab = picked;
        });

        SwitchOffCommand = new RelayCommand(tab =>
        {
            if (tab is not TabViewModel picked)
                return;
            if (Plugins.FirstOrDefault(p => string.Equals(p.Id, picked.Key, StringComparison.OrdinalIgnoreCase)) is { } entry)
                entry.IsActivated = false;
        });
    }

    /// <summary>
    /// The window this model is showing in, if one is; set by the tray when it makes one and
    /// cleared when it goes. The plugins' file dialogs are put over it.
    /// </summary>
    public Avalonia.Controls.Window? Window { get; set; }

    /// <summary>Ctrl+K. Everything the window can do, from one box.</summary>
    public CommandPaletteViewModel Palette { get; }

    public RelayCommand OpenPaletteCommand { get; }

    /// <summary>Clicking a tab on the strip. Its own button rather than a TabControl's selection.</summary>
    public RelayCommand SelectTabCommand { get; }

    /// <summary>
    /// Switching a plugin off from its own tab, which is the same as unticking it on the Plugins
    /// tab: the tab closes and whatever it was running is cancelled. Only a plugin's tab can be
    /// switched off; the shell's own tabs are not optional.
    /// </summary>
    public RelayCommand SwitchOffCommand { get; }

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

        yield return new PaletteItem("🏠", text.Format("palette.goto", text["shell.tab.home"]), "", () => SelectedTab = Tabs[0], weight: 1);
        if (_pluginsTab is { } pluginsTab)
            yield return new PaletteItem("⛭", text.Format("palette.goto", text["shell.tab.plugins"]), "", () => SelectedTab = pluginsTab, weight: 1);
        if (_settingsTab is { } settings)
            yield return new PaletteItem("⚙", text.Format("palette.goto", text["shell.tab.settings"]), "", () => SelectedTab = settings, weight: 1);
        if (_historyTab is { } history)
            yield return new PaletteItem("≡", text.Format("palette.goto", text["shell.tab.history"]), "", () => SelectedTab = history, weight: 1);

        if (_settingsViewModel is { } sv)
        {
            yield return new PaletteItem("◐", text["palette.theme.dark"], text["settings.theme"], () => sv.SetTheme(Appearance.Dark)) { IsCommand = true };
            yield return new PaletteItem("◑", text["palette.theme.light"], text["settings.theme"], () => sv.SetTheme(Appearance.Light)) { IsCommand = true };
            yield return new PaletteItem("◎", text["palette.theme.system"], text["settings.theme"], () => sv.SetTheme(Appearance.System)) { IsCommand = true };
            yield return new PaletteItem("Aa", text["palette.language.en"], text["settings.language"], () => sv.SetLanguage("en")) { IsCommand = true };
            yield return new PaletteItem("Aa", text["palette.language.de"], text["settings.language"], () => sv.SetLanguage("de")) { IsCommand = true };
            yield return new PaletteItem("Aa", text["palette.language.system"], text["settings.language"], () => sv.SetLanguage("system")) { IsCommand = true };
            foreach (var size in TabSizes.All)
            {
                var pick = size;
                yield return new PaletteItem("↕", text["settings.tabsize." + pick], text["settings.tabsize"],
                    () => sv.SetTabSize(pick)) { IsCommand = true };
            }
        }

        yield return new PaletteItem("🐾", text[PluginNames.Feline ? "palette.names.plain" : "palette.names.feline"], text["settings.names"],
            () => PluginNames.Feline = !PluginNames.Feline) { IsCommand = true };
        yield return new PaletteItem("🔔", text["palette.notifications"], "", () => IsNotificationsOpen = !IsNotificationsOpen) { IsCommand = true };
        yield return new PaletteItem("⏳", text["palette.tasks"], "", () => IsTasksOpen = !IsTasksOpen) { IsCommand = true };
        yield return new PaletteItem("≣", text["palette.log"], "", () => IsLogVisible = !IsLogVisible) { IsCommand = true };

        // Under > only: things to do to the window and the plugins, not places to go.
        foreach (var tab in Tabs)
        {
            var captured = tab;
            yield return captured.IsPoppedOut
                ? new PaletteItem("⧉", text.Format("palette.bringback", captured.Header), "", () => _popOuts.BringBack(captured)) { IsCommand = true, CommandOnly = true }
                : new PaletteItem("⧉", text.Format("palette.popout", captured.Header), "", () => _popOuts.PopOut(captured)) { IsCommand = true, CommandOnly = true };
        }
        // The groups on the strip, so shutting one is a keystroke rather than a right-click.
        foreach (var group in Strip.Groups)
        {
            var key = group.Key;
            var name = Strip.NameOf(key);
            yield return new PaletteItem("●",
                text.Format(group.IsCollapsed ? "strip.expand.one" : "strip.collapse.one", name), "",
                () => Strip.SetCollapsed(key, !group.IsCollapsed)) { IsCommand = true, CommandOnly = true };
        }

        yield return new PaletteItem("●", text["palette.strip.reset"], "", () => Strip.ResetAll()) { IsCommand = true, CommandOnly = true };
        yield return new PaletteItem("⛭", text["plugins.rescan"], "", Rescan) { IsCommand = true, CommandOnly = true };
        yield return new PaletteItem("⛭", text["plugins.open"], "", OpenPluginsFolder) { IsCommand = true, CommandOnly = true };
    }

    /// <summary>Ctrl+Shift+K: the palette over only the tab in front.</summary>
    public void OpenPaletteForFrontTab()
    {
        if (SelectedTab is { } tab)
            Palette.OpenScoped(tab.Key, tab.Header);
        else
            Palette.Open();
    }

    /// <summary>
    /// Ctrl+1 to Ctrl+9: the tabs in the order they are shown, which since groups arrived means
    /// the ones that can be seen. Counting past a collapsed group would make the number depend
    /// on something not on screen.
    /// </summary>
    public void SelectTabByNumber(int number)
    {
        var visible = Strip.Visible;
        if (number >= 1 && number <= visible.Count)
            SelectedTab = visible[number - 1];
    }

    /// <summary>
    /// What was typed, looked for inside every open plugin and then in the history. A plugin's
    /// own hits come first: a file in Kibble's grid is more likely what is wanted than the line
    /// saying it was queued last week. Each hit brings that tab to the front and lets the plugin
    /// put the thing on screen.
    /// </summary>
    private IEnumerable<PaletteItem> PaletteSearch(string query)
    {
        // Scoped to the tab in front: only that plugin's answer, or the history's when that is
        // the tab, and nothing from the plugins that are off.
        var scope = Palette.ScopeKey;

        foreach (var (id, tab) in _pluginTabs)
        {
            if (scope is not null && !string.Equals(scope, tab.Key, StringComparison.OrdinalIgnoreCase))
                continue;
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

        if (scope is not null && scope != "shell.tab.history")
            yield break;

        // Plugins that are off, through what they said they could answer without a view. Asked
        // once and kept; a plugin that answers null is not asked again until the list is read.
        foreach (var entry in scope is null ? Plugins.Where(p => p.IsCompatible && !_pluginTabs.ContainsKey(p.Id)) : [])
        {
            if (!_dormant.TryGetValue(entry.Id, out var asleep))
            {
                try
                {
                    var host = new DormantHost(entry.Id, _settings, new HandoffService(entry.Id, CanReach, SendHandoff), _store?.For(entry.Id));
                    asleep = entry.Descriptor.Plugin!.WhileOff(host);
                }
                catch (Exception ex)
                {
                    _log.Write("shell", $"'{entry.DisplayName}' failed to offer a search while off: {ex.Message}");
                    asleep = null;
                }
                _dormant[entry.Id] = asleep;
            }

            if (asleep is null)
                continue;

            IReadOnlyList<SearchHit> hits;
            try
            {
                hits = asleep.Search(query, 4);
            }
            catch (Exception ex)
            {
                _log.Write("shell", $"'{entry.DisplayName}' failed to search while off: {ex.Message}");
                continue;
            }

            var name = entry.DisplayName;
            foreach (var hit in hits)
            {
                var open = hit.Open;
                yield return new PaletteItem(entry.Icon, hit.Title, hit.Detail.Length > 0 ? $"{name} · {hit.Detail}" : name, () =>
                {
                    try
                    {
                        open();
                    }
                    catch (Exception ex)
                    {
                        _log.Write("shell", $"'{name}' failed to open a search hit while off: {ex.Message}");
                    }
                }, weight: -1);
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
        _home?.Retranslate();
        _logTab?.Retranslate();
        foreach (var entry in Plugins)
            entry.Rename();
        Regroup();
        foreach (var tab in Tabs)
            tab.Retranslate();

        foreach (var entry in Plugins)
            entry.Retranslate();

        Regroup();
        Strip?.Retranslate();

        OnPropertyChanged(nameof(ContractVersionText));
        OnPropertyChanged(nameof(PluginsDirectoryText));
        RaiseNotificationState();
        RaiseTaskState();
    }

    public ObservableCollection<TabViewModel> Tabs { get; } = new();

    /// <summary>The Settings tab's own model, for the palette and for the tests that drive it.</summary>
    public SettingsViewModel? Settings => _settingsViewModel;

    /// <summary>
    /// The strip over those tabs: Home, then a coloured chip and its tabs per group. Twenty-three
    /// tabs in one flat row wrapped onto three of them, so the strip groups by the same category
    /// a plugin already declares for its card on the Plugins tab.
    /// </summary>
    public TabStripViewModel Strip { get; private set; } = null!;

    public ObservableCollection<PluginEntryViewModel> Plugins { get; } = new();

    /// <summary>The same plugins under their headings, which is what the tab actually shows.</summary>
    public ObservableCollection<PluginGroupViewModel> PluginGroups { get; } = new();

    /// <summary>The bottom pane shows what the Log tab shows: the same per-source levels apply.</summary>
    public ObservableCollection<LogEntry> LogLines => _logTab.Visible;

    private LogViewModel _logTab = null!;

    public ObservableCollection<NotificationItem> Notifications => _notifications.Items;

    public ObservableCollection<BackgroundTaskItem> RunningTasks => _background.Running;

    public RelayCommand RescanCommand { get; }

    public RelayCommand OpenPluginsFolderCommand { get; }

    public RelayCommand OpenShellUpdateCommand { get; }

    /// <summary>A tab into a window of its own, from the button on its header.</summary>
    public RelayCommand PopOutCommand { get; }

    /// <summary>Back into the tab, from the notice left in its place.</summary>
    public RelayCommand BringBackCommand { get; }

    /// <summary>
    /// The tabs that were out when Meows last quit go out again, once, when there is a window
    /// to measure the screens from. A plugin tab that is not activated yet is simply not there
    /// to pop; it comes back out the next time it is, if it was still remembered.
    /// </summary>
    public void RestorePopOuts()
    {
        if (_popOutsRestored)
            return;
        _popOutsRestored = true;
        foreach (var key in _popOuts.RememberedOut.ToList())
        {
            if (Tabs.FirstOrDefault(t => t.Key == key) is { } tab)
                _popOuts.PopOut(tab);
        }
    }

    /// <summary>For the tray, which changes the preferences it shares with this model.</summary>
    public void SavePreferencesNow() => SavePreferences();

    private void SavePreferences()
    {
        try
        {
            _settings.SavePreferences(_preferences);
        }
        catch (Exception ex)
        {
            _log.Write("shell", $"Could not save preferences: {ex.Message}", LogLevel.Warning);
        }
    }

    /// <summary>What the last install said, under the buttons on the Plugins tab.</summary>
    public string? InstallNotice
    {
        get => _installNotice;
        private set
        {
            if (SetField(ref _installNotice, value))
                OnPropertyChanged(nameof(HasInstallNotice));
        }
    }

    public bool HasInstallNotice => !string.IsNullOrEmpty(_installNotice);

    /// <summary>
    /// A plugin's zip into the folder the shell scans, then a rescan so its card appears. A
    /// plugin that is already here is already loaded, so its new version is put beside it and
    /// the rescan swaps them if Windows lets the old folder be renamed; the notice says which
    /// happened, since the rescan knows and the install did not.
    /// </summary>
    public void InstallPlugin(string zipPath, string? source = null)
    {
        if (_catalog.InstallDirectory is not { } directory)
        {
            InstallNotice = _text.Format("plugins.install.failed", _text["plugins.nodirectory"]);
            return;
        }

        var report = PluginInstaller.Install(zipPath, directory, source);
        if (!report.Ok)
        {
            InstallNotice = _text.Format("plugins.install.failed", report.Error);
            _log.Write("plugins", $"Could not install {zipPath}: {report.Error}", LogLevel.Warning);
            return;
        }

        _log.Write("plugins", report.Pending
            ? $"Installed {report.Name} from {zipPath} beside the version that is loaded."
            : $"Installed {report.Name} from {zipPath}.");
        Rescan();

        var key = !report.Pending ? "plugins.install.done"
            : PluginInstaller.IsWaiting(directory, report.Name!) ? "plugins.install.pending"
            : "plugins.install.replaced";
        InstallNotice = _text.Format(key, report.Name);
    }

    /// <summary>
    /// The card's second click. The plugin is switched off first, so its work stops and its
    /// tab goes, then its folder is moved aside and the list is read again without it. Its
    /// settings stay where they are: a plugin that comes back finds them.
    /// </summary>
    private void Uninstall(PluginEntryViewModel entry)
    {
        var name = entry.DisplayName;
        var folder = entry.Descriptor.Folder;

        if (_pluginTabs.ContainsKey(entry.Id))
            Deactivate(entry);

        if (PluginInstaller.Uninstall(folder) is { } error)
        {
            InstallNotice = _text.Format("plugins.install.failed", error);
            _log.Write("plugins", $"Could not uninstall {name} from {folder}: {error}", LogLevel.Warning);
            Rescan();
            return;
        }

        _log.Write("plugins", $"Uninstalled {name}; its folder {folder} is gone or goes at the next start.");
        _availableUpdates.Remove(entry.Id);
        Rescan();
        PersistActivations();
        InstallNotice = _text.Format("plugins.uninstall.done", name);
    }

    /// <summary>The shell's own id in the update list; never a plugin's.</summary>
    private const string ShellId = "meows.shell";

    /// <summary>A newer Meows on the releases page, said in the status bar; null until one is found.</summary>
    public string? ShellUpdateText => _shellUpdate is { } update ? _text.Format("shell.update.available", update.Version) : null;

    public bool HasShellUpdate => _shellUpdate is not null;

    /// <summary>Opens the release page. Replacing a running exe is not something the shell does to itself.</summary>
    public void OpenShellUpdate()
    {
        if (_shellUpdate is not { } update)
            return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = update.ReleaseUrl, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Write("shell", $"Could not open {update.ReleaseUrl}: {ex.Message}");
        }
    }

    /// <summary>
    /// Once a day, and once now: which of the plugins installed through this tab has a newer
    /// release on GitHub, and whether Meows itself has. Nothing is fetched here but the answer;
    /// the card offers a plugin's update and the download waits for a click, and the shell's is
    /// a line in the status bar that opens the release page.
    /// </summary>
    private async Task CheckForUpdates(IBackgroundContext context)
    {
        var candidates = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => Plugins
            .Where(p => p.IsInstalled && p.Provenance.Homepage is not null)
            .Select(p => new UpdateCandidate(p.Id, p.Provenance.Homepage!, p.Provenance.Version))
            .ToList());

        // The shell asks about itself the same way, from the repository stamped into its own
        // assembly, so a fork that changes the csproj asks about the fork.
        var self = PluginProvenance.Read(typeof(MainWindowViewModel).Assembly, typeof(MainWindowViewModel).Assembly.Location);
        if (self.Homepage is { } homepage)
            candidates.Add(new UpdateCandidate(ShellId, homepage, AppVersion));

        if (candidates.Count == 0)
        {
            context.Report(_text["plugins.update.none"]);
            return;
        }

        var (updates, said) = await _updater.CheckAsync(candidates, context.Token);
        foreach (var line in said)
            _log.Write("plugins", line);

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            _availableUpdates.Clear();
            foreach (var (id, update) in updates)
                _availableUpdates[id] = update;
            OfferUpdates();

            _shellUpdate = updates.GetValueOrDefault(ShellId);
            OnPropertyChanged(nameof(ShellUpdateText));
            OnPropertyChanged(nameof(HasShellUpdate));
            OpenShellUpdateCommand.RaiseCanExecuteChanged();
            if (_shellUpdate is { } newer)
                _log.Write("shell", $"Meows {newer.Version} is on the releases page; this is {AppVersionText}.");
        });

        var plugins = candidates.Count(c => c.Id != ShellId);
        context.Report(plugins == 0
            ? _text[_shellUpdate is null ? "plugins.update.none" : "shell.update.only"]
            : _text.Format("plugins.update.checked", plugins, updates.Count(u => u.Key != ShellId)));
    }

    /// <summary>Puts what the last check found onto the cards, and takes it off a card that has caught up.</summary>
    private void OfferUpdates()
    {
        foreach (var entry in Plugins)
        {
            if (_availableUpdates.TryGetValue(entry.Id, out var update) && PluginUpdates.IsNewer(update.Version, entry.Provenance.Version))
                entry.Update = update;
            else
            {
                _availableUpdates.Remove(entry.Id);
                entry.Update = null;
            }
        }
        // The shell's own line is not a card's; a plugin cannot take that id.
        if (_shellUpdate is { } shell)
            _availableUpdates[ShellId] = shell;
    }

    /// <summary>The card's Update button: fetch the zip as a task, then hand it to the installer.</summary>
    private void UpdatePlugin(PluginEntryViewModel entry)
    {
        if (entry.Update is not { } update)
            return;

        var name = entry.DisplayName;
        _background.RunForShell(_text.Format("plugins.update.download", name), async context =>
        {
            context.Report(update.ZipName);
            var path = await _updater.DownloadAsync(update, new ProgressTo(context), context.Token);
            try
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => InstallPlugin(path, update.ReleaseUrl));
            }
            finally
            {
                try { File.Delete(path); } catch (Exception) { }
            }
        });
    }

    private sealed class ProgressTo(IBackgroundContext context) : IProgress<double?>
    {
        public void Report(double? value) => context.ReportProgress(value);
    }

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
        set
        {
            var was = _selectedTab;
            if (!SetField(ref _selectedTab, value))
                return;

            // The strip is an ItemsControl now, so nothing marks the item in front for us.
            if (was is not null)
                was.IsSelected = false;
            if (value is not null)
                value.IsSelected = true;

            if (value is null)
                return;

            // Landing on a tab whose group is shut opens that group: a selection nobody can see
            // is worse than a group that opened itself.
            Strip?.Reveal(value.Key);

            // Home is read, not watched: whatever a plugin has to say for itself is asked for
            // when the tab comes to the front, so a glance is as fresh as the moment it is seen.
            if (Tabs.Count > 0 && ReferenceEquals(value, Tabs[0]))
                _home?.Refresh();
        }
    }

    /// <summary>
    /// What a tab says its group is: the plugin's own category, the same one its card sits under
    /// on the Plugins tab. Null for anything that is not a plugin's tab, which the strip reads as
    /// the shell's own group or as everything else.
    /// </summary>
    private string? CategoryOf(string tabKey) =>
        Plugins.FirstOrDefault(p => string.Equals(p.Id, tabKey, StringComparison.OrdinalIgnoreCase))?.Descriptor.Category;

    /// <summary>
    /// The plugin's own line for its Home card, if its view model offers one. A glance that
    /// throws is logged and the card keeps the shell's line; it is one sentence on a summary
    /// page, not something to fail the page over.
    /// </summary>
    private Glance? GlanceAt(PluginEntryViewModel entry)
    {
        if (!_pluginTabs.TryGetValue(entry.Id, out var tab))
            return null;
        var glanceable = (tab.Content as Avalonia.Controls.Control)?.DataContext as IGlanceable
                         ?? tab.Content as IGlanceable;
        if (glanceable is null)
            return null;
        try
        {
            return glanceable.Glance();
        }
        catch (Exception ex)
        {
            _log.Write("shell", $"'{entry.DisplayName}' failed to glance: {ex.Message}");
            return null;
        }
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
        var (item, action) = parameter switch
        {
            NotificationButton button => (button.Item, button.Action),
            NotificationItem { Action: { } first } single => (single, first),
            _ => (null, null),
        };
        if (item is null || action is null)
            return;

        try
        {
            action.Invoke();
        }
        catch (Exception ex)
        {
            // It is plugin code. Do not let it take the shell down.
            _log.Write("shell", $"Notification action '{action.Label}' threw: {ex}");
            return;
        }

        // A button on a toast usually means "dealt with". A condition stays: only its plugin
        // knows whether it still applies, and its own action will clear it if it does not.
        if (action.DismissesAfter && item.CanDismiss)
            _notifications.Dismiss(item);
    }

    private void CancelTask(object? parameter)
    {
        if (parameter is BackgroundTaskItem task)
            task.Cancel();
    }

    /// <summary>Version off the assembly, so the status bar always names the running build.</summary>
    /// <summary>Just the number, for comparing and for the User-Agent.</summary>
    public static string AppVersion
    {
        get
        {
            var v = typeof(MainWindowViewModel).Assembly.GetName().Version;
            return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
        }
    }

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
        // The strip watches the tabs, so it exists before any of them are added.
        Strip = new TabStripViewModel(Tabs, _preferences, CategoryOf, SavePreferences);

        // Home first: the window is opened after hours, and the first thing it shows is what
        // happened. Plugins second, so an empty plugins folder is still one click from help.
        _home = new HomeViewModel(Plugins, p => _pluginTabs.ContainsKey(p.Id), GlanceAt, OpenPlugin, _notifications, _background,
            _store, PluginName, () => _preferences.LastSeen, InvokeNotificationAction);
        Tabs.Add(new TabViewModel("shell.tab.home", "🏠", new HomeView { DataContext = _home }));
        _pluginsTab = new TabViewModel("shell.tab.plugins", "⛭", new PluginsView { DataContext = this });
        Tabs.Add(_pluginsTab);
        _settingsViewModel = new SettingsViewModel(_settings, _text, _log, _preferences)
        {
            TabSizeChanged = () => Strip.Resize(),
        };
        _settingsTab = new TabViewModel("shell.tab.settings", "⚙", new SettingsView { DataContext = _settingsViewModel });
        Tabs.Add(_settingsTab);
        if (_store is not null)
        {
            _history = new HistoryViewModel(_store, PluginName, UndoTargetFor, ShowPlugin);
            _historyTab = new TabViewModel("shell.tab.history", "≡", new HistoryView { DataContext = _history });
            Tabs.Add(_historyTab);
        }

        _logTab = new LogViewModel(_log, ReadLogLevels(), SaveLogLevels);
        Tabs.Add(new TabViewModel("shell.tab.log", "≣", new LogView { DataContext = _logTab }));
        OnPropertyChanged(nameof(LogLines));
        SelectedTab = Tabs[0];
        _background.RestartActionFor = RestartActionFor;
        Rescan();

        _home?.Refresh();

        // Only plugins put there through this tab are asked about, so on most machines this
        // pass says "nothing to check" and touches no network at all.
        _background.ScheduleForShell(_text["plugins.update.task"], TimeSpan.FromHours(24), CheckForUpdates, runImmediately: true);
    }

    private void Rescan()
    {
        foreach (var entry in Plugins.Where(p => p.IsActivated).ToList())
            Deactivate(entry);

        Plugins.Clear();
        _dormant.Clear();

        var activated = _settings.LoadActivatedPlugins();
        foreach (var descriptor in _catalog.Discover())
        {
            // Its strings before its card, not when it is switched on. A card carries the
            // plugin's own description, so waiting for activation left every inactive plugin
            // introducing itself with a dotted key.
            if (descriptor.Plugin is { } plugin)
                _text.Add(plugin.GetType().Assembly);

            Plugins.Add(new PluginEntryViewModel(descriptor, OnActivationChanged, HealthOf, Uninstall, UpdatePlugin));
        }

        Regroup();
        OfferUpdates();

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

    /// <summary>
    /// The button on a "task failed" notification. Off and on again is what restarts a stopped
    /// schedule, and doing it from the notification saves the trip to the Plugins tab.
    /// </summary>
    private NotificationAction? RestartActionFor(string pluginId)
    {
        var entry = Plugins.FirstOrDefault(p => string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        if (entry is null || !entry.IsCompatible)
            return null;

        return new NotificationAction(_text["notify.restart"], () =>
        {
            if (_pluginTabs.ContainsKey(entry.Id))
                Deactivate(entry);
            Activate(entry);
            entry.SetActivatedSilently(true);
            PersistActivations();
            _log.Write("shell", $"Restarted '{entry.DisplayName}' from a notification.");
        }, DismissesAfter: true);
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
                new HandoffService(entry.Id, CanReach, SendHandoff), _store?.For(entry.Id), _picker);
            _dormant.Remove(entry.Id);
            _sourceById[entry.Id] = entry.DisplayName;
            var view = entry.Descriptor.Plugin!.CreateView(host);
            var tab = new TabViewModel(entry.Id, () => entry.DisplayName, entry.Icon, view) { IsPlugin = true };
            _pluginTabs[entry.Id] = tab;
            Tabs.Add(tab);
            SelectedTab = tab;
            // Was in its own window last time and is switched on again: out it goes.
            if (_popOutsRestored && _popOuts.RememberedOut.Contains(entry.Id))
                _popOuts.PopOut(tab);
            entry.Error = null;
            _log.Write("shell", $"Activated '{entry.DisplayName}'.");
            _home?.Refresh();
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

    /// <summary>The card's health line: the last journal entry and the watches, for one plugin.</summary>
    private PluginHealth HealthOf(string pluginId)
    {
        var last = _store?.Events(pluginId, null, null, 1).FirstOrDefault();
        var watches = _background.Watches()
            .Where(w => string.Equals(w.PluginId, pluginId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return PluginHealth.Describe(last, watches);
    }

    private void OnRecorded(string pluginId) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        Plugins.FirstOrDefault(p => string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase))?.RefreshHealth();
    });

    private void OnWatchesChanged()
    {
        foreach (var entry in Plugins)
            entry.RefreshHealth();
    }

    private IReadOnlyDictionary<string, LogLevel> ReadLogLevels()
    {
        var levels = new Dictionary<string, LogLevel>(StringComparer.OrdinalIgnoreCase);
        foreach (var (source, level) in _preferences.LogLevels)
        {
            levels[source] = level switch
            {
                "warning" => LogLevel.Warning,
                "quiet" => LogViewModel.Quiet,
                _ => LogLevel.Info,
            };
        }
        return levels;
    }

    private void SaveLogLevels(IReadOnlyDictionary<string, LogLevel> levels)
    {
        _preferences.LogLevels = levels.ToDictionary(
            p => p.Key,
            p => p.Value == LogViewModel.Quiet ? "quiet" : p.Value == LogLevel.Warning ? "warning" : "info",
            StringComparer.OrdinalIgnoreCase);
        _settings.SavePreferences(_preferences);
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
            target.Receive(WithReplyOnUiThread(handoff, fromId, entry.DisplayName));
            _log.Write("shell", $"{fromId} handed {handoff.Paths.Count} path(s) to '{entry.DisplayName}'.");
            return true;
        }
        catch (Exception ex)
        {
            _log.Write("shell", $"'{entry.DisplayName}' failed to take a handoff: {ex}");
            return false;
        }
    }

    /// <summary>
    /// The sender's Reply, made safe for a receiver to call from wherever its work finishes: it
    /// lands on the UI thread, runs once, and is logged, so the sender's status line can bind
    /// to it without thinking about threads.
    /// </summary>
    private Handoff WithReplyOnUiThread(Handoff handoff, string fromId, string toName)
    {
        if (handoff.Reply is not { } reply)
            return handoff;

        var answered = 0;
        return handoff with
        {
            Reply = outcome => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (Interlocked.Exchange(ref answered, 1) != 0)
                    return;
                _log.Write("shell", $"'{toName}' answered {fromId}: {outcome}");
                try
                {
                    reply(outcome);
                }
                catch (Exception ex)
                {
                    _log.Write("shell", $"{fromId} could not take the reply: {ex.Message}", LogLevel.Warning);
                }
            }),
        };
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

        _popOuts.Forget(tab);
        Tabs.Remove(tab);
        (tab.Content as IDisposable)?.Dispose();
        if ((tab.Content as Avalonia.StyledElement)?.DataContext is IDisposable disposableContext)
            disposableContext.Dispose();

        SelectedTab ??= Tabs.FirstOrDefault();
        _log.Write("shell", $"Deactivated '{entry.DisplayName}'.");
        _home?.Refresh();
    }

    private void PersistActivations() =>
        _settings.SaveActivatedPlugins(Plugins.Where(p => p.IsActivated).Select(p => p.Id));

    /// <summary>
    /// The window went away, to the tray or for good: from now is "since you were away". Saved
    /// at once, because a quit is the other way this happens and there is no later then.
    /// </summary>
    public void MarkSeen()
    {
        _preferences.LastSeen = DateTime.Now;
        SavePreferences();
        _home?.Refresh();
    }

    public void Shutdown()
    {
        _popOuts.CloseAll();
        MarkSeen();
        foreach (var entry in Plugins.Where(p => p.IsActivated).ToList())
            Deactivate(entry);
        _background.Dispose();
        _home?.Dispose();

        // The name switch is a static event; a model that has gone must stop hearing it, or
        // the next flip reaches into a window that is no longer there.
        PluginNames.Changed -= OnNamesChanged;
        _notifications.Changed -= RaiseNotificationState;
        _background.Changed -= RaiseTaskState;
        _background.WatchesChanged -= OnWatchesChanged;
        if (_store is not null)
            _store.Recorded -= OnRecorded;
    }
}
