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

    /// <summary>
    /// What the plugin is, in plain words, for people who would rather read "Duplicates" than
    /// "Purrge". Return a key from your strings catalogue to have it translated. The switch that
    /// picks between the two is the shell's; the default is the feline name, so a plugin that
    /// does not say has the same name in both modes.
    /// </summary>
    string PlainName => DisplayName;

    /// <summary>
    /// One sentence, shown on the Plugins tab. Return a key from your strings catalogue to have
    /// it translated; anything that is not a key is shown exactly as written, which is what a
    /// plugin with no catalogue does.
    /// </summary>
    string Description { get; }

    /// <summary>Glyph next to the tab header. Null just shows the name.</summary>
    string? Icon { get; }

    /// <summary>
    /// Which heading this appears under on the Plugins tab. Null puts it with everything else
    /// that did not say, which is why this has a default: a plugin written against 0.1.0 keeps
    /// compiling and keeps loading, it simply does not join a group.
    ///
    /// The shell does not interpret the text. Two plugins are in the same group when they spell
    /// it the same way, and nothing here knows which groups are supposed to exist. Grouping is
    /// done on what you return here, and only the heading itself is translated, so two plugins
    /// sharing a group still share it in every language.
    /// </summary>
    string? Category => null;

    /// <summary>
    /// Called once per activation. The control, and its DataContext, get disposed on
    /// deactivation if they implement IDisposable.
    /// </summary>
    Control CreateView(IMeowsHost host);

    /// <summary>
    /// Something to search while this plugin is switched off, or null to be left out of Ctrl+K
    /// until it is on, which is what a plugin that does not say gets. Called when the palette
    /// first needs it and kept until the plugin is switched on or the list is read again, so
    /// read what you keep once, here, and answer from memory. The host has your settings, your
    /// journal and a way to hand yourself the thing that was found; see
    /// <see cref="IMeowsDormantHost"/>. Since 1.0.0.
    /// </summary>
    ISearchable? WhileOff(IMeowsDormantHost host) => null;

    /// <summary>
    /// What a rule can ask this plugin to do, performed by the view model through
    /// <see cref="IActionTarget"/>. Read while the plugin is off, so answer with a fixed list,
    /// never from settings or the disk. Empty, the default, keeps the plugin out of the "then"
    /// half of a rule. Since 1.2.0.
    /// </summary>
    IReadOnlyList<PluginAction> Actions => [];

    /// <summary>
    /// The kinds of event this plugin records, so a rule can wait for one before it has ever
    /// happened. A kind not listed here can still start a rule once it is in the history; this
    /// is the list with words for people, and it is read while the plugin is off. Since 1.2.0.
    /// </summary>
    IReadOnlyList<RecordedKind> Records => [];
}
