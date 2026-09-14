using Meows.Plugins.Abstractions;
using Meows.Plugins;

namespace Meows.ViewModels;

/// <summary>One card on the Plugins tab. Toggling it adds or removes that plugin's tab.</summary>
public sealed class PluginEntryViewModel : ObservableObject
{
    private readonly Action<PluginEntryViewModel, bool> _onActivationChanged;
    private bool _isActivated;
    private string? _error;

    private readonly Func<string, PluginHealth>? _health;

    public PluginEntryViewModel(PluginDescriptor descriptor, Action<PluginEntryViewModel, bool> onActivationChanged,
        Func<string, PluginHealth>? health = null)
    {
        Descriptor = descriptor;
        _onActivationChanged = onActivationChanged;
        _health = health;
    }

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
