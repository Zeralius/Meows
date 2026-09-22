using System.Reflection;
using Meows.Services;

namespace Meows.Plugins;

/// <summary>
/// Where a plugin came from, read from what the build already stamps into its assembly and from
/// the note the installer leaves in its folder. None of it needs the contract: the SDK writes
/// the version, the authors and the repository URL into every DLL when the csproj names them,
/// so a plugin built elsewhere can say who made it without knowing Meows would ask.
/// </summary>
public sealed record PluginProvenance(
    string? Version,
    string? Author,
    string? Homepage,
    InstallRecord? Installed)
{
    public static PluginProvenance None { get; } = new(null, null, null, null);

    /// <summary>Put there through the Plugins tab, so it can be taken away through it.</summary>
    public bool IsInstalled => Installed is not null;

    /// <summary>Reads the assembly's attributes and the folder's marker. Never throws: a plugin with nothing to say gets nothing.</summary>
    public static PluginProvenance Read(Assembly? assembly, string assemblyPath)
    {
        var folder = Path.GetDirectoryName(assemblyPath);
        var installed = folder is null ? null : PluginInstaller.RecordOf(folder);
        if (assembly is null)
            return new PluginProvenance(null, null, null, installed);

        string? version = null, author = null, homepage = null;
        try
        {
            version = Tidy(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
            // The SDK appends the commit as +hash unless told not to; nobody wants that on a card.
            if (version is not null && version.IndexOf('+') is > 0 and var plus)
                version = version[..plus];

            // Company defaults to the assembly name when the csproj names no author, and an
            // author called "Meows.Plugins.Kibble" is no author at all.
            author = Tidy(assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company);
            if (author is not null && author.Equals(assembly.GetName().Name, StringComparison.OrdinalIgnoreCase))
                author = null;

            homepage = Tidy(assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key.Equals("RepositoryUrl", StringComparison.OrdinalIgnoreCase))?.Value);
            if (homepage is not null && !homepage.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                homepage = null;
        }
        catch (Exception)
        {
            // An attribute that will not construct is not worth refusing a plugin over.
        }

        return new PluginProvenance(version, author, homepage, installed);
    }

    /// <summary>The version as something to compare: 1.2.3 out of "v1.2.3-beta+sha", or null.</summary>
    public static System.Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var core = text.Trim().TrimStart('v', 'V');
        var cut = core.IndexOfAny(['-', '+']);
        if (cut > 0)
            core = core[..cut];
        return System.Version.TryParse(core, out var version) ? version : null;
    }

    private static string? Tidy(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
