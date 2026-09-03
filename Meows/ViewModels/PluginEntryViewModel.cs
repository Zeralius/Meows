using Meows.Plugins.Abstractions;
using Meows.Plugins;

namespace Meows.ViewModels;

/// <summary>One card on the Plugins tab. Toggling it adds or removes that plugin's tab.</summary>
public sealed class PluginEntryViewModel : ObservableObject
{
    private readonly Action<PluginEntryViewModel, bool> _onActivationChanged;
    private bool _isActivated;
    private string? _error;

    public PluginEntryViewModel(PluginDescriptor descriptor, Action<PluginEntryViewModel, bool> onActivationChanged)
    {
        Descriptor = descriptor;
        _onActivationChanged = onActivationChanged;
    }

    public PluginDescriptor Descriptor { get; }

    public string Id => Descriptor.Id;

    public string DisplayName => Descriptor.DisplayName;

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
    }
}
