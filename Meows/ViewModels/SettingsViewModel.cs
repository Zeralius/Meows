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

    public SettingsViewModel(ShellSettings settings, Translations text, ShellLog log, ShellPreferences preferences)
    {
        _settings = settings;
        _text = text;
        _log = log;
        _preferences = preferences;

        OpenSettingsFolderCommand = new RelayCommand(OpenSettingsFolder);
    }

    public RelayCommand OpenSettingsFolderCommand { get; }

    /// <summary>Where the two files behind this tab actually live.</summary>
    public string SettingsFolder => _settings.Root;

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

    private void SetTheme(string choice)
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

    private void SetLanguage(string choice)
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
