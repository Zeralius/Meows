using System.Reflection;
using Meows.Plugins.Abstractions;

namespace Meows.Plugins;

/// <summary>
/// Whether this shell can run a plugin, based on the contract version it was built against.
///
/// The runtime will not warn us. The contract is shared with the shell rather than loaded per
/// plugin, so .NET happily binds a plugin built against 0.2.0 to our 0.1.0 and then throws
/// <see cref="MissingMethodException"/> somewhere unhelpful later. Better to refuse up front and
/// show the reason on the plugin's card.
/// </summary>
public static class ContractCompatibility
{
    public const string ContractAssemblyName = "Meows.Plugins.Abstractions";

    /// <summary>The contract version this shell provides.</summary>
    public static Version ShellVersion { get; } =
        typeof(IMeowsPlugin).Assembly.GetName().Version ?? new Version(0, 0, 0);

    public static string ShellVersionText => Format(ShellVersion);

    /// <summary>Drops the fourth number MSBuild adds, so 0.1.0.0 reads as 0.1.0.</summary>
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
    /// Avalonia is shared with the shell for the same reason the contract is: a plugin returns
    /// a <c>Control</c>, and two copies of Avalonia mean two unrelated types with that name. So
    /// its version matters as much as the contract's, and nothing was checking it.
    ///
    /// It has never bitten us because every plugin in this solution builds against the same
    /// reference. Plugins written elsewhere are where it would start.
    /// </summary>
    public static Version ShellUiVersion { get; } =
        typeof(Avalonia.AvaloniaObject).Assembly.GetName().Version ?? new Version(0, 0, 0);

    public static string ShellUiVersionText => Format(ShellUiVersion);

    /// <summary>
    /// Which Avalonia this assembly was built against, or null if it uses none. Avalonia
    /// versions all its assemblies together, so the highest referenced one is the answer.
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
            return MeowsText.Current.Format("contract.ui.major", Format(pluginUi), ShellUiVersionText);

        if (pluginUi > ShellUiVersion)
            return MeowsText.Current.Format("contract.ui.newer", Format(pluginUi), ShellUiVersionText);

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
            return MeowsText.Current.Format("contract.major", Format(pluginContract), ShellVersionText);

        // Newer within the same major may call members we do not have. Older is fine,
        // since additions stay backward compatible.
        if (pluginContract > ShellVersion)
            return MeowsText.Current.Format("contract.newer", Format(pluginContract), ShellVersionText);

        return null;
    }
}
