using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media;
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
        CustomizeCommand = new RelayCommand(Customize);
        ResetAppearanceCommand = new RelayCommand(ResetAppearance);
        _editDark = _preferences.Theme == Appearance.Dark;
        UpdateAppearanceEditor();
    }

    public RelayCommand OpenSettingsFolderCommand { get; }

    public RelayCommand CustomizeCommand { get; }

    public RelayCommand ResetAppearanceCommand { get; }

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

    /// <summary>One-off notifications as Windows notifications while the window is not in front.</summary>
    public bool SayOutside
    {
        get => _preferences.SayOutside;
        set
        {
            if (_preferences.SayOutside == value)
                return;
            _preferences.SayOutside = value;
            _settings.SavePreferences(_preferences);
            OnPropertyChanged();
        }
    }

    /// <summary>The week counted from the history, as one notification a week.</summary>
    public bool WeeklyRecap
    {
        get => _preferences.WeeklyRecap;
        set
        {
            if (_preferences.WeeklyRecap == value)
                return;
            _preferences.WeeklyRecap = value;
            _settings.SavePreferences(_preferences);
            OnPropertyChanged();
        }
    }

    /// <summary>Which surface this machine got, said rather than failed quietly.</summary>
    public string OutsideHow => MeowsText.Current[Toasts.Surface switch
    {
        ToastSurface.Toast => "settings.outside.toast",
        ToastSurface.Balloon => ShellSettings.IsPortable ? "settings.outside.balloon.portable" : "settings.outside.balloon",
        _ => "settings.outside.none",
    }];

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
        Appearance.ApplyScheme(_preferences);
        Save();
        UpdateAppearanceEditor();

        OnPropertyChanged(nameof(IsThemeSystem));
        OnPropertyChanged(nameof(IsThemeLight));
        OnPropertyChanged(nameof(IsThemeDark));
    }

    /// <summary>The accents to pick from, default first.</summary>
    public ObservableCollection<AccentChoice> Accents { get; } =
    [
        new(Appearance.DefaultAccent, "settings.accent.default"),
        new("#0078D4", "settings.accent.blue"),
        new("#107C10", "settings.accent.green"),
        new("#CA5010", "settings.accent.amber"),
        new("#C239B3", "settings.accent.rose"),
        new("#8764B8", "settings.accent.violet"),
        new("#038387", "settings.accent.teal"),
    ];

    /// <summary>
    /// The same thing as a dropdown row. A ComboBox hands back the object it was given, and
    /// the object it was given has to be one of the very items in the list or it shows blank.
    /// </summary>
    public AccentChoice? SelectedAccent
    {
        get => Accents.FirstOrDefault(a => string.Equals(a.Hex, Appearance.NormaliseAccent(_preferences.Accent), StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is not null)
                SetAccent(value.Hex);
        }
    }

    public void SetAccent(string choice)
    {
        choice = Appearance.NormaliseAccent(choice);
        if (_preferences.Accent == choice)
            return;

        _preferences.Accent = choice;
        Appearance.ApplyScheme(_preferences);
        Save();
        UpdateAppearanceEditor();

        OnPropertyChanged(nameof(SelectedAccent));
    }

    public ObservableCollection<AppearanceSchemeChoice> Schemes { get; } =
        new(Appearance.Schemes.Select(s => new AppearanceSchemeChoice(s.Id, s.Label)));

    public ObservableCollection<AppearanceColourRow> Colours { get; } = [];

    public AppearancePreview Preview { get; } = new();

    public AppearanceSchemeChoice? SelectedScheme
    {
        get => Schemes.FirstOrDefault(s => s.Id == Appearance.NormaliseScheme(_preferences.ColourScheme));
        set
        {
            if (value is null || value.Id == Appearance.NormaliseScheme(_preferences.ColourScheme))
                return;
            if (value.Id == Appearance.CustomScheme)
            {
                Customize();
                return;
            }
            _preferences.ColourScheme = value.Id;
            _preferences.Accent = Appearance.DefaultAccent;
            Appearance.ApplyScheme(_preferences);
            Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedAccent));
            UpdateAppearanceEditor();
        }
    }

    public bool IsCustomScheme => Appearance.NormaliseScheme(_preferences.ColourScheme) == Appearance.CustomScheme;

    private bool _editDark;

    public bool EditDark
    {
        get => _editDark;
        set
        {
            if (!SetField(ref _editDark, value))
                return;
            OnPropertyChanged(nameof(EditLight));
            UpdateAppearanceEditor();
        }
    }

    public bool EditLight
    {
        get => !EditDark;
        set { if (value) EditDark = false; }
    }

    public string ContrastNotice
    {
        get
        {
            var low = Appearance.LowContrast(Preview.Colours);
            return low.Count == 0 ? "" : _text.Format("settings.scheme.contrast", string.Join(", ", low.Select(slot => _text["settings.colour." + slot])));
        }
    }

    public bool HasContrastNotice => ContrastNotice.Length > 0;

    private void Customize()
    {
        if (!IsCustomScheme)
        {
            _preferences.CustomLight = Appearance.Resolve(_preferences, dark: false);
            _preferences.CustomDark = Appearance.Resolve(_preferences, dark: true);
        }
        _preferences.ColourScheme = Appearance.CustomScheme;
        _preferences.Accent = Appearance.DefaultAccent;
        Appearance.ApplyScheme(_preferences);
        Save();
        OnPropertyChanged(nameof(SelectedScheme));
        OnPropertyChanged(nameof(SelectedAccent));
        UpdateAppearanceEditor();
    }

    private void ResetAppearance()
    {
        _preferences.Theme = Appearance.System;
        _preferences.ColourScheme = Appearance.OriginalScheme;
        _preferences.Accent = Appearance.DefaultAccent;
        _preferences.CustomLight = null;
        _preferences.CustomDark = null;
        Appearance.Apply(_preferences.Theme);
        Appearance.ApplyScheme(_preferences);
        Save();
        OnPropertyChanged(nameof(IsThemeSystem));
        OnPropertyChanged(nameof(IsThemeLight));
        OnPropertyChanged(nameof(IsThemeDark));
        OnPropertyChanged(nameof(SelectedScheme));
        OnPropertyChanged(nameof(SelectedAccent));
        UpdateAppearanceEditor();
    }

    private void SetCustomColour(string slot, string hex)
    {
        if (!IsCustomScheme || !Appearance.TryHex(hex, out var valid))
            return;
        if (EditDark)
            _preferences.CustomDark = Appearance.WithColour(_preferences.CustomDark ?? Appearance.Preset(Appearance.OriginalScheme, true), slot, valid);
        else
            _preferences.CustomLight = Appearance.WithColour(_preferences.CustomLight ?? Appearance.Preset(Appearance.OriginalScheme, false), slot, valid);
        if (slot == "accent")
        {
            _preferences.Accent = Appearance.DefaultAccent;
            OnPropertyChanged(nameof(SelectedAccent));
        }
        Appearance.ApplyScheme(_preferences);
        Save();
        Preview.Colours = Appearance.Resolve(_preferences, EditDark);
        OnPropertyChanged(nameof(ContrastNotice));
        OnPropertyChanged(nameof(HasContrastNotice));
    }

    private void UpdateAppearanceEditor()
    {
        Preview.Colours = Appearance.Resolve(_preferences, EditDark);
        Colours.Clear();
        if (IsCustomScheme)
            foreach (var slot in Appearance.ColourSlots)
                Colours.Add(new AppearanceColourRow(slot, Appearance.Colour(Preview.Colours, slot), SetCustomColour));
        OnPropertyChanged(nameof(IsCustomScheme));
        OnPropertyChanged(nameof(ContrastNotice));
        OnPropertyChanged(nameof(HasContrastNotice));
    }

    public void Retranslate()
    {
        foreach (var scheme in Schemes)
            scheme.Reread();
        foreach (var accent in Accents)
            accent.Reread();
        foreach (var colour in Colours)
            colour.Reread();
        OnPropertyChanged(nameof(ContrastNotice));
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

/// <summary>One accent in the dropdown, named by the language the window is in.</summary>
public sealed class AccentChoice(string hex, string key) : ObservableObject
{
    public string Hex { get; } = hex;

    public string Name => MeowsText.Current[key];

    internal void Reread() => OnPropertyChanged(nameof(Name));
}

public sealed class AppearanceSchemeChoice(string id, string key) : ObservableObject
{
    public string Id { get; } = id;

    public string Name => MeowsText.Current[key];

    internal void Reread() => OnPropertyChanged(nameof(Name));
}

public sealed class AppearanceColourRow : ObservableObject
{
    private readonly Action<string, string> _changed;
    private string _hex;
    private string _lastValid;

    public AppearanceColourRow(string slot, string hex, Action<string, string> changed)
    {
        Slot = slot;
        _hex = hex;
        _lastValid = hex;
        _changed = changed;
    }

    public string Slot { get; }

    public string Name => MeowsText.Current["settings.colour." + Slot];

    public string Hex
    {
        get => _hex;
        set
        {
            if (!SetField(ref _hex, value ?? ""))
                return;
            if (Appearance.TryHex(_hex, out var valid))
            {
                _lastValid = valid;
                _changed(Slot, valid);
                OnPropertyChanged(nameof(Swatch));
            }
            OnPropertyChanged(nameof(IsInvalid));
        }
    }

    public bool IsInvalid => !Appearance.TryHex(_hex, out _);

    public string ErrorText => MeowsText.Current["settings.scheme.invalid"];

    public IBrush Swatch => new SolidColorBrush(Color.Parse(_lastValid));

    internal void Reread()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(ErrorText));
    }
}

public sealed class AppearancePreview : ObservableObject
{
    private SchemeColours _colours = Appearance.Preset(Appearance.OriginalScheme, false);

    public SchemeColours Colours
    {
        get => _colours;
        set
        {
            _colours = value;
            OnEverythingChanged();
        }
    }

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    public IBrush Background => Brush(Colours.Background);
    public IBrush Panel => Brush(Colours.Panel);
    public IBrush Card => Brush(Colours.Card);
    public IBrush Foreground => Brush(Colours.Foreground);
    public IBrush Border => Brush(Colours.Border);
    public IBrush Selection => Brush(Colours.Selection);
    public IBrush SelectionText => Brush(Colours.SelectionText);
    public IBrush Accent => Brush(Colours.Accent);
    public IBrush Info => Brush(Colours.Info);
    public IBrush InfoText => Brush(Colours.InfoText);
    public IBrush Warning => Brush(Colours.Warning);
    public IBrush WarningText => Brush(Colours.WarningText);
    public IBrush Danger => Brush(Colours.Danger);
    public IBrush DangerText => Brush(Colours.DangerText);
}
