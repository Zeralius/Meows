using Avalonia.Controls;

namespace Meows.Plugins.Abstractions;

/// <summary>
/// One plugin, one tab. The shell finds these by scanning the plugins folder for public
/// types with a parameterless constructor.
/// </summary>
public interface IMeowsPlugin
{
    /// <summary>Keep this stable. It is the settings key and the activation record.</summary>
    string Id { get; }

    string DisplayName { get; }

    string Description { get; }

    /// <summary>Glyph next to the tab header. Null just shows the name.</summary>
    string? Icon { get; }

    /// <summary>
    /// Which heading this appears under on the Plugins tab. Null puts it with everything else
    /// that did not say, which is why this has a default: a plugin written against 0.1.0 keeps
    /// compiling and keeps loading, it simply does not join a group.
    ///
    /// The shell does not interpret the text. Two plugins are in the same group when they spell
    /// it the same way, and nothing here knows which groups are supposed to exist.
    /// </summary>
    string? Category => null;

    /// <summary>
    /// Called once per activation. The control, and its DataContext, get disposed on
    /// deactivation if they implement IDisposable.
    /// </summary>
    Control CreateView(IMeowsHost host);
}
