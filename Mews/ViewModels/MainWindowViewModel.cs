using Mews.Plugins.Abstractions;
using System.Collections.ObjectModel;
using Mews.Plugins;
using Mews.Services;
using Mews.Views;

namespace Mews.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly PluginCatalog _catalog;
    private readonly ShellSettings _settings;
    private readonly ShellLog _log;
    private readonly NotificationCenter _notifications;
    private readonly BackgroundTaskService _background;
    private readonly Dictionary<string, TabViewModel> _pluginTabs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _sourceById = new(StringComparer.OrdinalIgnoreCase);

    private TabViewModel? _selectedTab;
    private bool _isLogVisible;
    private bool _isNotificationsOpen;
    private bool _isTasksOpen;

    public MainWindowViewModel(
        PluginCatalog catalog,
        ShellSettings settings,
        ShellLog log,
        NotificationCenter notifications,
        BackgroundTaskService background)
    {
        _catalog = catalog;
        _settings = settings;
        _log = log;
        _notifications = notifications;
        _background = background;

        RescanCommand = new RelayCommand(Rescan);
        ToggleLogCommand = new RelayCommand(() => IsLogVisible = !IsLogVisible);
        ToggleNotificationsCommand = new RelayCommand(() => IsNotificationsOpen = !IsNotificationsOpen);
        ToggleTasksCommand = new RelayCommand(() => IsTasksOpen = !IsTasksOpen);
        DismissNotificationCommand = new RelayCommand(DismissNotification);
        InvokeNotificationActionCommand = new RelayCommand(InvokeNotificationAction);
        ClearNotificationsCommand = new RelayCommand(() => _notifications.DismissAllEvents());
        CancelTaskCommand = new RelayCommand(CancelTask);

        _notifications.Changed += RaiseNotificationState;
        _background.Changed += RaiseTaskState;
    }

    public ObservableCollection<TabViewModel> Tabs { get; } = new();

    public ObservableCollection<PluginEntryViewModel> Plugins { get; } = new();

    public ObservableCollection<string> LogLines => _log.Lines;

    public ObservableCollection<NotificationItem> Notifications => _notifications.Items;

    public ObservableCollection<BackgroundTaskItem> RunningTasks => _background.Running;

    public RelayCommand RescanCommand { get; }

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
        ? "Alerts"
        : $"{Glyph(_notifications.Worst)} {_notifications.Count}";

    public bool HasRunningTasks => _background.RunningCount > 0;

    public string TaskBadge => _background.RunningCount == 0
        ? "Tasks"
        : $"Tasks {_background.RunningCount}";

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
            return v is null ? "Mews" : $"Mews {v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
        }
    }

    public string ContractVersionText => $"Plugin contract {ContractCompatibility.ShellVersionText}";

    public bool HasIncompatiblePlugins => Plugins.Any(p => p.IsIncompatible);

    public string PluginsDirectoryText =>
        _catalog.PluginsDirectory ?? "No plugins directory found (set MEWS_PLUGINS_DIR)";

    public bool HasPlugins => Plugins.Count > 0;

    public void Initialize()
    {
        // Always tab zero, so an empty plugins folder still opens on something useful.
        Tabs.Add(new TabViewModel("Plugins", "⛭", new PluginsView { DataContext = this }));
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
            Plugins.Add(new PluginEntryViewModel(descriptor, OnActivationChanged));

        OnPropertyChanged(nameof(PluginsDirectoryText));
        OnPropertyChanged(nameof(HasPlugins));

        foreach (var entry in Plugins.Where(p => activated.Contains(p.Id)))
            entry.IsActivated = true;
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
            var host = new PluginHost(entry.Id, entry.DisplayName, _settings, _log, _notifications, _background);
            _sourceById[entry.Id] = entry.DisplayName;
            var view = entry.Descriptor.Plugin!.CreateView(host);
            var tab = new TabViewModel(entry.DisplayName, entry.Icon, view);
            _pluginTabs[entry.Id] = tab;
            Tabs.Add(tab);
            SelectedTab = tab;
            entry.Error = null;
            _log.Write("shell", $"Activated '{entry.DisplayName}'.");
        }
        catch (Exception ex)
        {
            // Mark it failed and carry on. One bad plugin should not close the window.
            entry.Error = ex.Message;
            entry.SetActivatedSilently(false);
            _log.Write("shell", $"'{entry.DisplayName}' failed to activate: {ex}");
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
