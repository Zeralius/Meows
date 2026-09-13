namespace Meows.Plugins.Abstractions;

/// <summary>
/// The two names every plugin has, and which one is showing.
///
/// Every plugin is named after the cat the app is named for, which is a pleasure to the person
/// who named them and a puzzle to anyone else looking for the duplicate finder. So each plugin
/// also says what it is in plain words, and one switch, on the Settings tab and in the bottom
/// bar, decides which name is on the tabs and the cards. The feline name stays the identity
/// underneath: notification sources, task sources and the log keep using it, because those are
/// keys and a key that changes when a switch is flipped is not a key.
/// </summary>
public static class PluginNames
{
    private static readonly Dictionary<string, string> PlainByFeline = new(StringComparer.Ordinal);
    private static bool _feline = true;

    /// <summary>True shows Purrge; false shows Duplicates. On by default, since it is the app's character.</summary>
    public static bool Feline
    {
        get => _feline;
        set
        {
            if (_feline == value)
                return;
            _feline = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Raised when the switch is flipped. Anything that shows a name reads it again.</summary>
    public static event Action? Changed;

    /// <summary>Called by the shell for each plugin it finds, so <see cref="Display"/> can answer for a bare feline name.</summary>
    public static void Register(IMeowsPlugin plugin) => PlainByFeline[plugin.DisplayName] = plugin.PlainName;

    /// <summary>The name to show for this plugin right now.</summary>
    public static string For(IMeowsPlugin plugin) => Feline ? plugin.DisplayName : MeowsText.Current[plugin.PlainName];

    /// <summary>
    /// The name to show for a feline name that was captured as a key: a notification's source, a
    /// task's owner. Comes back unchanged when the switch is on or the name was never registered.
    /// </summary>
    public static string Display(string felineName) =>
        !Feline && PlainByFeline.TryGetValue(felineName, out var plainKey)
            ? MeowsText.Current[plainKey]
            : felineName;

    /// <summary>The name that is not showing, for a card that says both.</summary>
    public static string Other(IMeowsPlugin plugin) => Feline ? MeowsText.Current[plugin.PlainName] : plugin.DisplayName;
}
