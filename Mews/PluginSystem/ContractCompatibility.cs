using System.Reflection;
using Mews.Plugins.Abstractions;

namespace Mews.Plugins;

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
    public const string ContractAssemblyName = "Mews.Plugins.Abstractions";

    /// <summary>What we ship.</summary>
    public static Version ShellVersion { get; } =
        typeof(IMewsPlugin).Assembly.GetName().Version ?? new Version(0, 0, 0);

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

    /// <summary>Null means load it. Anything else is the reason we will not.</summary>
    public static string? Check(Version? pluginContract)
    {
        if (pluginContract is null)
            return null; // Not a contract consumer; the type scan will simply find nothing.

        // A major bump can remove or change members, so an older major is no safer than a
        // newer one.
        if (pluginContract.Major != ShellVersion.Major)
            return $"Built for Mews contract {Format(pluginContract)}, but this shell provides " +
                   $"{ShellVersionText}. Major versions must match.";

        // Newer within the same major may call members we do not have. Older is fine,
        // since additions stay backward compatible.
        if (pluginContract > ShellVersion)
            return $"Built for Mews contract {Format(pluginContract)}, which is newer than this " +
                   $"shell's {ShellVersionText}. Update Mews, or rebuild the plugin against {ShellVersionText}.";

        return null;
    }
}
