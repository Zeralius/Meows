using Avalonia.Controls;

namespace Mews.Plugins.Abstractions;

/// <summary>
/// One plugin, one tab. The shell finds these by scanning the plugins folder for public
/// types with a parameterless constructor.
/// </summary>
public interface IMewsPlugin
{
    /// <summary>Keep this stable. It is the settings key and the activation record.</summary>
    string Id { get; }

    string DisplayName { get; }

    string Description { get; }

    /// <summary>Glyph next to the tab header. Null just shows the name.</summary>
    string? Icon { get; }

    /// <summary>
    /// Called once per activation. The control, and its DataContext, get disposed on
    /// deactivation if they implement IDisposable.
    /// </summary>
    Control CreateView(IMewsHost host);
}
