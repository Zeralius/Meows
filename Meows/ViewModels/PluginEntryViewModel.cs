using System.Diagnostics;
using Meows.Plugins.Abstractions;
using Meows.Plugins;
using Meows.Services;

namespace Meows.ViewModels;

/// <summary>One card on the Plugins tab. Toggling it adds or removes that plugin's tab.</summary>
public sealed class PluginEntryViewModel : ObservableObject
{
    private readonly Action<PluginEntryViewModel, bool> _onActivationChanged;
    private readonly Action<PluginEntryViewModel>? _onUninstall;
    private readonly Action<PluginEntryViewModel>? _onUpdate;
    private bool _isActivated;
    private string? _error;
    private bool _isConfirmingUninstall;
    private AvailableUpdate? _update;

    private readonly Func<string, PluginHealth>? _health;

    public PluginEntryViewModel(PluginDescriptor descriptor, Action<PluginEntryViewModel, bool> onActivationChanged,
        Func<string, PluginHealth>? health = null,
        Action<PluginEntryViewModel>? onUninstall = null,
        Action<PluginEntryViewModel>? onUpdate = null)
    {
        Descriptor = descriptor;
        _onActivationChanged = onActivationChanged;
        _health = health;
        _onUninstall = onUninstall;
        _onUpdate = onUpdate;
        OpenHomepageCommand = new RelayCommand(OpenHomepage, () => HasHomepage);
        UninstallCommand = new RelayCommand(Uninstall, () => IsInstalled && _onUninstall is not null);
        UpdateCommand = new RelayCommand(() => _onUpdate?.Invoke(this), () => HasUpdate && _onUpdate is not null);
    }

    // ---- Where it came from -------------------------------------------------------------

    public PluginProvenance Provenance => Descriptor.Provenance;

    /// <summary>Put there through the Plugins tab, so the card offers to take it away again.</summary>
    public bool IsInstalled => Provenance.IsInstalled;

    /// <summary>Version, author and when it was installed, whichever the plugin can say, on one line.</summary>
    public string ProvenanceText
    {
        get
        {
            var text = MeowsText.Current;
            var parts = new List<string>();
            if (Provenance.Version is { } version)
                parts.Add(version);
            if (Provenance.Author is { } author)
                parts.Add(author);
            if (Provenance.Installed is { } installed)
                parts.Add(text.Format("plugins.installed.on", installed.On.ToString("d")));
            return string.Join(" · ", parts);
        }
    }

    public bool HasProvenance => ProvenanceText.Length > 0;

    public bool HasHomepage => Provenance.Homepage is not null;

    /// <summary>The address without its scheme, which is how people read one.</summary>
    public string HomepageText =>
        Provenance.Homepage is { } url
            ? url.Replace("https://", "").Replace("http://", "").TrimEnd('/')
            : "";

    public RelayCommand OpenHomepageCommand { get; }

    private void OpenHomepage()
    {
        if (Provenance.Homepage is not { } url)
            return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception)
        {
            // The browser is not ours to fix; the address is on the card to copy.
        }
    }

    // ---- Uninstall, in two clicks ----------------------------------------------------------

    /// <summary>
    /// The first click only changes the button's words; the second does it. A dialog would be
    /// the usual way, and a button that asks in place is the same safety with less furniture.
    /// </summary>
    public RelayCommand UninstallCommand { get; }

    public bool IsConfirmingUninstall
    {
        get => _isConfirmingUninstall;
        private set
        {
            if (SetField(ref _isConfirmingUninstall, value))
                OnPropertyChanged(nameof(UninstallText));
        }
    }

    public string UninstallText => MeowsText.Current[IsConfirmingUninstall ? "plugins.uninstall.sure" : "plugins.uninstall"];

    private void Uninstall()
    {
        if (!IsConfirmingUninstall)
        {
            IsConfirmingUninstall = true;
            return;
        }

        IsConfirmingUninstall = false;
        _onUninstall?.Invoke(this);
    }

    /// <summary>Back to the plain button, for when something else happened in between.</summary>
    public void CancelUninstall() => IsConfirmingUninstall = false;

    // ---- A newer release ---------------------------------------------------------------------

    public AvailableUpdate? Update
    {
        get => _update;
        set
        {
            if (!SetField(ref _update, value))
                return;
            OnPropertyChanged(nameof(HasUpdate));
            OnPropertyChanged(nameof(UpdateText));
            UpdateCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasUpdate => _update is not null;

    public string UpdateText => _update is { } update ? MeowsText.Current.Format("plugins.update.available", update.Version) : "";

    public RelayCommand UpdateCommand { get; }

    /// <summary>
    /// What the plugin last did and what it is watching, from the store and the shell's list of
    /// schedules, so "is Birdwatch actually doing anything" is answered where its switch is.
    /// </summary>
    public PluginHealth Health => _health?.Invoke(Id) ?? PluginHealth.Nothing;

    public string HealthText => Health.Text;

    public bool HasHealth => Health.Text.Length > 0;

    public bool HealthIsTrouble => Health.StoppedWatches > 0;

    public void RefreshHealth()
    {
        OnPropertyChanged(nameof(Health));
        OnPropertyChanged(nameof(HealthText));
        OnPropertyChanged(nameof(HasHealth));
        OnPropertyChanged(nameof(HealthIsTrouble));
    }

    public PluginDescriptor Descriptor { get; }

    public string Id => Descriptor.Id;

    /// <summary>The name showing now. The feline one stays the identity in the log and the notification sources.</summary>
    public string DisplayName => Descriptor.Name;

    public string OtherName => Descriptor.OtherName;

    public bool HasOtherName => Descriptor.OtherName.Length > 0;

    /// <summary>The names read differently now: the switch flipped, or the language did.</summary>
    public void Rename()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(OtherName));
        OnPropertyChanged(nameof(HasOtherName));
    }

    /// <summary>
    /// Translated if the plugin returned a key, and left alone if it returned a sentence. A
    /// plugin with no catalogue is the second case and reads the same as it always did.
    /// </summary>
    public string Description => MeowsText.Current[Descriptor.Description];

    public string Icon => Descriptor.Icon;

    public string Origin => Descriptor.Origin;

    /// <summary>False if we refused it on contract grounds. The toggle is hidden in that case.</summary>
    public bool IsCompatible => Descriptor.IsCompatible;

    public string? IncompatibleReason => Descriptor.IncompatibleReason;

    public bool IsIncompatible => !IsCompatible;

    public bool IsActivated
    {
        get => _isActivated;
        set
        {
            if (!SetField(ref _isActivated, value))
                return;
            _onActivationChanged(this, value);
            OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>Sets the flag without triggering activation. For restoring saved state.</summary>
    public void SetActivatedSilently(bool value)
    {
        _isActivated = value;
        OnPropertyChanged(nameof(IsActivated));
        OnPropertyChanged(nameof(StatusText));
    }

    public string? Error
    {
        get => _error;
        set
        {
            if (SetField(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(Error);

    public string StatusText => IsIncompatible ? MeowsText.Current["plugins.incompatible"]
        : HasError ? MeowsText.Current["plugins.failed"]
        : IsActivated ? MeowsText.Current["plugins.active"] : MeowsText.Current["plugins.inactive"];

    /// <summary>Called after a language change. Only the strings we own are affected.</summary>
    public void Retranslate()
    {
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ProvenanceText));
        OnPropertyChanged(nameof(UninstallText));
        OnPropertyChanged(nameof(UpdateText));
        RefreshHealth();
    }
}

/// <summary>One line about a plugin: its last journal entry and its watches, in words.</summary>
public sealed record PluginHealth(string Text, int Watches, int StoppedWatches)
{
    public static PluginHealth Nothing { get; } = new("", 0, 0);

    /// <summary>The words, from the last thing it recorded and the state of its schedules.</summary>
    public static PluginHealth Describe(StoredEvent? last, IReadOnlyList<WatchInfo> watches)
    {
        var text = MeowsText.Current;
        var parts = new List<string>();

        if (last is not null)
        {
            var what = last.Detail is { Length: > 0 } detail ? detail : last.Kind;
            var name = Path.GetFileName(last.Subject) is { Length: > 0 } file ? file : last.Subject;
            parts.Add(text.Format("plugins.health.last", name, what, Ago(last.At)));
        }

        var stopped = watches.Count(w => w.IsStopped);
        if (watches.Count > 0)
        {
            var running = watches.Count - stopped;
            var line = running == 1 ? text["plugins.health.watch.one"] : text.Format("plugins.health.watch.many", running);
            if (stopped > 0)
                line += " " + text.Format("plugins.health.watch.stopped", stopped);
            parts.Add(line);
        }

        return new PluginHealth(string.Join(" · ", parts), watches.Count, stopped);
    }

    private static string Ago(DateTime when)
    {
        var text = MeowsText.Current;
        var span = DateTime.Now - when;
        if (span.TotalMinutes < 1) return text["plugins.health.justnow"];
        if (span.TotalMinutes < 90) return text.Format("plugins.health.minutes", (int)span.TotalMinutes);
        if (span.TotalHours < 36) return text.Format("plugins.health.hours", (int)span.TotalHours);
        return text.Format("plugins.health.days", (int)span.TotalDays);
    }
}
