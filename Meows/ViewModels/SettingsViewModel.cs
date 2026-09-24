using System.Diagnostics;
using Meows.Plugins.Abstractions;
using Meows.Services;

namespace Meows.ViewModels;

/// <summary>
/// The Settings tab. Two choices, both of which take effect as you make them rather than on the
/// next start, and both of which are written out straight away so closing the window mid-thought
/// does not lose them.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly ShellSettings _settings;
    private readonly Translations _text;
    private readonly ShellLog _log;
    private readonly ShellPreferences _preferences;

    /// <summary>Told after the tab size changes, so the strip redraws at the new one.</summary>
    public Action? TabSizeChanged { get; set; }

    /// <summary>The Server section: where plugins can copy to. Null in a test that did not give one.</summary>
    public ServerViewModel? Server { get; init; }

    public bool HasServer => Server is not null;

    public SettingsViewModel(ShellSettings settings, Translations text, ShellLog log, ShellPreferences preferences)
    {
        _settings = settings;
        _text = text;
        _log = log;
        _preferences = preferences;

        OpenSettingsFolderCommand = new RelayCommand(OpenSettingsFolder);
    }

    public RelayCommand OpenSettingsFolderCommand { get; }

    /// <summary>
    /// How long history is kept, as radio buttons want it: one bool each, acted on for the
    /// true side only. Zero is forever, and the default, since the store kept everything before
    /// anyone could choose.
    /// </summary>
    public int HistoryKeepDays
    {
        get => _preferences.HistoryKeepDays;
        set
        {
            if (_preferences.HistoryKeepDays == value)
                return;
            _preferences.HistoryKeepDays = value;
            _settings.SavePreferences(_preferences);
            _log.Write("settings", value == 0 ? "History is kept forever." : $"History is kept for {value} days.");
            OnPropertyChanged();
            OnPropertyChanged(nameof(KeepForever));
            OnPropertyChanged(nameof(KeepAYear));
            OnPropertyChanged(nameof(KeepSixMonths));
            OnPropertyChanged(nameof(KeepThreeMonths));
            OnPropertyChanged(nameof(KeepAMonth));
        }
    }

    public bool KeepForever { get => HistoryKeepDays == 0; set { if (value) HistoryKeepDays = 0; } }

    public bool KeepAYear { get => HistoryKeepDays == 365; set { if (value) HistoryKeepDays = 365; } }

    public bool KeepSixMonths { get => HistoryKeepDays == 182; set { if (value) HistoryKeepDays = 182; } }

    public bool KeepThreeMonths { get => HistoryKeepDays == 91; set { if (value) HistoryKeepDays = 91; } }

    public bool KeepAMonth { get => HistoryKeepDays == 30; set { if (value) HistoryKeepDays = 30; } }

    /// <summary>
    /// Read from the registry each time rather than remembered here, so this cannot drift from
    /// what Windows will actually do. Somebody switching it off in the Task Manager should show
    /// up on this tab, not be quietly overwritten by a stale copy of the answer.
    /// </summary>
    private StartupRegistration Startup => StartWithWindows.Read();

    /// <summary>Feline or plain plugin names. The static switch is the source; the main window saves it.</summary>
    public bool FelineNames
    {
        get => PluginNames.Feline;
        set
        {
            PluginNames.Feline = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Whether the close button hides the window or quits. Read by the tray at the moment of
    /// closing, so a change here takes effect on the very next close.
    /// </summary>
    public bool CloseToTray
    {
        get => _preferences.CloseToTray;
        set
        {
            if (_preferences.CloseToTray == value)
                return;
            _preferences.CloseToTray = value;
            _settings.SavePreferences(_preferences);
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Rewrites the startup entry with or without --tray. Kept in the preferences too, so the tick
    /// reads right even while startup itself is off.
    /// </summary>
    public bool StartInTray
    {
        get => _preferences.StartInTray;
        set
        {
            if (_preferences.StartInTray == value)
                return;
            _preferences.StartInTray = value;
            _settings.SavePreferences(_preferences);
            OnPropertyChanged();

            if (Startup.IsOn)
                StartsWithWindows = true;
        }
    }

    public bool StartsWithWindows
    {
        get => Startup.IsOn;
        set
        {
            if (StartWithWindows.Set(value, _preferences.StartInTray) is { } problem)
            {
                _log.Write("shell", $"Could not change the startup list: {problem}");
                StartupProblem = _text.Format("settings.startup.failed", problem);
            }
            else
            {
                StartupProblem = null;
            }

            RaiseStartup();
        }
    }

    /// <summary>Where the entry will point, which is worth seeing before it is written.</summary>
    public string StartupPath => StartWithWindows.ExecutablePath ?? "";

    public bool CanStartWithWindows => StartWithWindows.ExecutablePath is not null;

    private string? _startupProblem;

    public string? StartupProblem
    {
        get => _startupProblem;
        private set => SetField(ref _startupProblem, value);
    }

    /// <summary>
    /// What is worth saying about the entry beyond the tick: pointing at another copy, switched
    /// off by Windows, or nothing at all.
    /// </summary>
    public string StartupNote
    {
        get
        {
            if (_startupProblem is { } problem)
                return problem;

            var registration = Startup;
            return registration.State switch
            {
                StartupState.Elsewhere =>
                    _text.Format("settings.startup.elsewhere", registration.RegisteredPath ?? ""),
                StartupState.BlockedByWindows => _text["settings.startup.blocked"],
                StartupState.Unavailable when !CanStartWithWindows =>
                    _text["settings.startup.unavailable"],
                _ => "",
            };
        }
    }

    public bool HasStartupNote => StartupNote.Length > 0;

    private void RaiseStartup()
    {
        OnPropertyChanged(nameof(StartsWithWindows));
        OnPropertyChanged(nameof(StartupNote));
        OnPropertyChanged(nameof(HasStartupNote));
    }

    /// <summary>Where the two files behind this tab actually live.</summary>
    public string SettingsFolder => _settings.Root;

    /// <summary>Beside the exe because a file called portable sits there.</summary>
    public bool IsPortable => ShellSettings.IsPortable;

    /// <summary>
    /// Radio buttons want a bool each rather than one string, and a group of them fires the
    /// unticked one as well as the ticked one. Acting only on the true side keeps a switch to
    /// one save instead of two.
    /// </summary>
    public bool IsThemeSystem
    {
        get => _preferences.Theme == Appearance.System;
        set { if (value) SetTheme(Appearance.System); }
    }

    public bool IsThemeLight
    {
        get => _preferences.Theme == Appearance.Light;
        set { if (value) SetTheme(Appearance.Light); }
    }

    public bool IsThemeDark
    {
        get => _preferences.Theme == Appearance.Dark;
        set { if (value) SetTheme(Appearance.Dark); }
    }

    public bool IsTabsCompact
    {
        get => TabSizes.Tidy(_preferences.TabSize) == TabSizes.Compact;
        set { if (value) SetTabSize(TabSizes.Compact); }
    }

    public bool IsTabsNormal
    {
        get => TabSizes.Tidy(_preferences.TabSize) == TabSizes.Normal;
        set { if (value) SetTabSize(TabSizes.Normal); }
    }

    public bool IsTabsLarge
    {
        get => TabSizes.Tidy(_preferences.TabSize) == TabSizes.Large;
        set { if (value) SetTabSize(TabSizes.Large); }
    }

    public bool IsLanguageSystem
    {
        get => _preferences.Language == "system";
        set { if (value) SetLanguage("system"); }
    }

    public bool IsLanguageEnglish
    {
        get => _preferences.Language == "en";
        set { if (value) SetLanguage("en"); }
    }

    public bool IsLanguageGerman
    {
        get => _preferences.Language == "de";
        set { if (value) SetLanguage("de"); }
    }

    /// <summary>
    /// Says which language "follow the system" landed on, so the choice is not a mystery when the
    /// machine is set to something we do not ship.
    /// </summary>
    public string FollowingText =>
        _text.Format("settings.language.following", _text[$"language.name.{_text.Language}"]);

    public void SetTheme(string choice)
    {
        if (_preferences.Theme == choice)
            return;

        _preferences.Theme = choice;
        Appearance.Apply(choice);
        Save();

        OnPropertyChanged(nameof(IsThemeSystem));
        OnPropertyChanged(nameof(IsThemeLight));
        OnPropertyChanged(nameof(IsThemeDark));
    }

    public void SetTabSize(string choice)
    {
        choice = TabSizes.Tidy(choice);
        if (TabSizes.Tidy(_preferences.TabSize) == choice)
            return;

        _preferences.TabSize = choice;
        Save();
        TabSizeChanged?.Invoke();

        OnPropertyChanged(nameof(IsTabsCompact));
        OnPropertyChanged(nameof(IsTabsNormal));
        OnPropertyChanged(nameof(IsTabsLarge));
    }

    public void SetLanguage(string choice)
    {
        if (_preferences.Language == choice)
            return;

        _preferences.Language = choice;
        _text.Use(choice);
        Save();

        OnPropertyChanged(nameof(IsLanguageSystem));
        OnPropertyChanged(nameof(IsLanguageEnglish));
        OnPropertyChanged(nameof(IsLanguageGerman));
        OnPropertyChanged(nameof(FollowingText));
    }

    private void Save() => _settings.SavePreferences(_preferences);

    // ---- the whole folder as one zip, and back ----

    private string? _bundleNotice;

    /// <summary>What the last export or import said.</summary>
    public string? BundleNotice
    {
        get => _bundleNotice;
        private set
        {
            if (SetField(ref _bundleNotice, value))
                OnPropertyChanged(nameof(HasBundleNotice));
        }
    }

    public bool HasBundleNotice => !string.IsNullOrEmpty(_bundleNotice);

    /// <summary>A name for the file, with the date in it, so two exports do not become one.</summary>
    public string SuggestedBundleName => $"meows-settings-{DateTime.Now:yyyy-MM-dd}.zip";

    public void Export(string zipPath)
    {
        var report = SettingsBundle.Export(_settings.Root, zipPath, MainWindowViewModel.AppVersionText);
        BundleNotice = report.Ok
            ? _text.Format("settings.bundle.exported", report.Files, zipPath)
            : _text.Format("settings.bundle.failed", report.Error);
        _log.Write("settings", report.Ok ? $"Exported {report.Files} file(s) to {zipPath}" : $"Export failed: {report.Error}",
            report.Ok ? Plugins.Abstractions.LogLevel.Info : Plugins.Abstractions.LogLevel.Warning);
    }

    public void Import(string zipPath)
    {
        var report = SettingsBundle.Import(_settings.Root, zipPath, MainWindowViewModel.AppVersionText);
        BundleNotice = report.Ok
            ? _text.Format("settings.bundle.imported", report.Files, report.Skipped, Path.GetFileName(report.BackupPath))
            : _text.Format("settings.bundle.failed", report.Error);
        _log.Write("settings", report.Ok
            ? $"Imported {report.Files} file(s) from {zipPath}, {report.Skipped} skipped, previous settings kept in {report.BackupPath}"
            : $"Import failed: {report.Error}",
            report.Ok ? Plugins.Abstractions.LogLevel.Info : Plugins.Abstractions.LogLevel.Warning);
    }

    private void OpenSettingsFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = _settings.Root, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Write("shell", $"Could not open {_settings.Root}: {ex.Message}");
        }
    }
}
