using System.Reflection;
using Meows.Plugins.Abstractions;

namespace Meows.Plugins;

/// <summary>
/// Can this shell run this plugin, going by the contract version it was built against?
///
/// The runtime will not tell you. Since the contract is shared with the shell rather than
/// loaded from the plugin folder, .NET binds a plugin built against 0.2.0 to our 0.1.0
/// happily, then throws <see cref="MissingMethodException"/> somewhere useless later. Better
/// to say no up front and put the reason on the plugin's card.
/// </summary>
public static class ContractCompatibility
{
    public const string ContractAssemblyName = "Meows.Plugins.Abstractions";

    /// <summary>What we ship.</summary>
    public static Version ShellVersion { get; } =
        typeof(IMeowsPlugin).Assembly.GetName().Version ?? new Version(0, 0, 0);

    public static string ShellVersionText => Format(ShellVersion);

    /// <summary>Drops the fourth number MSBuild tacks on, so 0.1.0.0 reads as 0.1.0.</summary>
    public static string Format(Version version) =>
        $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";

    /// <summary>What the assembly was built against, or null if it does not use the contract.</summary>
    public static Version? ReferencedContractVersion(Assembly assembly)
    {
        try
        {
            return assembly.GetReferencedAssemblies()
                .FirstOrDefault(a => string.Equals(a.Name, ContractAssemblyName, StringComparison.OrdinalIgnoreCase))
                ?.Version;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Avalonia is shared with the shell for the same reason the contract is: a plugin hands back
    /// a <c>Control</c>, and two copies of Avalonia mean two unrelated types with that name. So
    /// the version it was built against matters exactly as much, and until now nothing looked.
    ///
    /// Nobody has met this yet because every plugin here is built in this solution against the
    /// same reference. A plugin written elsewhere is precisely where it starts happening.
    /// </summary>
    public static Version ShellUiVersion { get; } =
        typeof(Avalonia.AvaloniaObject).Assembly.GetName().Version ?? new Version(0, 0, 0);

    public static string ShellUiVersionText => Format(ShellUiVersion);

    /// <summary>
    /// The Avalonia this assembly was built against, or null if it uses none. Avalonia ships its
    /// assemblies on one version, so the highest of whatever is referenced is the answer.
    /// </summary>
    public static Version? ReferencedUiVersion(Assembly assembly)
    {
        try
        {
            return assembly.GetReferencedAssemblies()
                .Where(a => a.Name is not null &&
                            a.Name.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase))
                .Select(a => a.Version)
                .Where(v => v is not null)
                .Max();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Everything worth refusing a plugin over, in one question.</summary>
    public static string? CheckAssembly(Assembly assembly) =>
        Check(ReferencedContractVersion(assembly)) ?? CheckUi(ReferencedUiVersion(assembly));

    /// <summary>Null means the UI framework is close enough. Anything else is why it is not.</summary>
    public static string? CheckUi(Version? pluginUi)
    {
        if (pluginUi is null)
            return null; // Draws nothing, so there is nothing to disagree about.

        if (pluginUi.Major != ShellUiVersion.Major)
            return $"Built against Avalonia {Format(pluginUi)}, but this shell hosts " +
                   $"{ShellUiVersionText}. The two share one copy of Avalonia, so the major " +
                   "version has to match or the controls it builds are not controls this can show.";

        if (pluginUi > ShellUiVersion)
            return $"Built against Avalonia {Format(pluginUi)}, which is newer than the " +
                   $"{ShellUiVersionText} this shell provides. Update Meows, or rebuild the plugin " +
                   $"against Avalonia {ShellUiVersionText}.";

        return null;
    }

    /// <summary>Null means load it. Anything else is the reason we will not.</summary>
    public static string? Check(Version? pluginContract)
    {
        if (pluginContract is null)
            return null; // Not a contract consumer; the type scan will simply find nothing.

        // A major bump can remove or change members, so an older major is no safer than a
        // newer one.
        if (pluginContract.Major != ShellVersion.Major)
            return $"Built for Meows contract {Format(pluginContract)}, but this shell provides " +
                   $"{ShellVersionText}. Major versions must match.";

        // Newer within the same major may call members we do not have. Older is fine,
        // since additions stay backward compatible.
        if (pluginContract > ShellVersion)
            return $"Built for Meows contract {Format(pluginContract)}, which is newer than this " +
                   $"shell's {ShellVersionText}. Update Meows, or rebuild the plugin against {ShellVersionText}.";

        return null;
    }
}
