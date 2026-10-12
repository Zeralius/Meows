using System.Text.RegularExpressions;

namespace Meows.Plugins;

/// <summary>
/// What a plugin id may look like. An id becomes a folder name under the settings, a key in the
/// list of what is on and the scope background work is cancelled by, so "..", which made the
/// plugin's data folder the settings root itself, or "meows.shell", which would share the
/// shell's own work and have it cancelled when the plugin was switched off, are refused.
/// </summary>
public static partial class PluginIds
{
    /// <summary>The scope the shell's own background work runs under.</summary>
    public const string Shell = "meows.shell";

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,99}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Shape();

    public static bool IsValid(string? id) =>
        id is not null &&
        Shape().IsMatch(id) &&
        !id.Contains("..", StringComparison.Ordinal) &&
        !id.EndsWith('.') &&
        !string.Equals(id, Shell, StringComparison.OrdinalIgnoreCase);
}
