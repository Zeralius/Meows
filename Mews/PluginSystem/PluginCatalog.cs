using System.Reflection;
using Mews.Plugins.Abstractions;
using Mews.Services;

namespace Mews.Plugins;

/// <summary>Finds plugin DLLs and creates whatever they export.</summary>
public sealed class PluginCatalog
{
    private readonly ShellLog _log;

    public PluginCatalog(ShellLog log) => _log = log;

    public IReadOnlyList<string> PluginsDirectories { get; private set; } = [];

    /// <summary>Joined up for the status bar.</summary>
    public string? PluginsDirectory =>
        PluginsDirectories.Count == 0 ? null : string.Join(" ; ", PluginsDirectories);

    /// <summary>
    /// Where to look, in order: MEWS_PLUGINS_DIR, then a plugins folder next to the exe (the
    /// deployed layout), then one in a parent folder (what makes an in-solution build work).
    ///
    /// MEWS_PLUGINS_DIR is a <c>;</c> separated list and adds to the rest rather than
    /// replacing it, so you can develop a plugin out of tree without losing the built-in ones.
    /// </summary>
    public static IReadOnlyList<string> ResolvePluginsDirectories()
    {
        var directories = new List<string>();

        var fromEnv = Environment.GetEnvironmentVariable("MEWS_PLUGINS_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            foreach (var entry in fromEnv.Split(';', StringSplitOptions.RemoveEmptyEntries |
                                                     StringSplitOptions.TrimEntries))
            {
                try
                {
                    if (Directory.Exists(entry))
                        Add(directories, Path.GetFullPath(entry));
                }
                catch (Exception)
                {
                    // Skip a bad entry rather than losing the whole list.
                }
            }
        }

        var discovered = DiscoverDefaultDirectory();
        if (discovered is not null)
            Add(directories, discovered);

        return directories;
    }

    private static string? DiscoverDefaultDirectory()
    {
        // The deployed layout, so accept it even when empty.
        var beside = Path.Combine(AppContext.BaseDirectory, "plugins");
        if (Directory.Exists(beside))
            return beside;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "plugins");
            // Windows paths ignore case, so without this check a source folder called
            // Plugins wins over the real one. Require it to actually contain a plugin.
            if (Directory.Exists(candidate) && HoldsPluginFolders(candidate))
                return candidate;
            dir = dir.Parent;
        }

        return null;
    }

    private static void Add(List<string> directories, string path)
    {
        if (!directories.Contains(path, StringComparer.OrdinalIgnoreCase))
            directories.Add(path);
    }

    private static bool HoldsPluginFolders(string candidate)
    {
        try
        {
            return Directory.EnumerateDirectories(candidate)
                .Any(sub => Directory.EnumerateFiles(sub, "*.dll").Any());
        }
        catch (Exception)
        {
            return false;
        }
    }

    public IReadOnlyList<PluginDescriptor> Discover()
    {
        PluginsDirectories = ResolvePluginsDirectories();
        if (PluginsDirectories.Count == 0)
        {
            _log.Write("plugins", "No plugins directory found. Set MEWS_PLUGINS_DIR to point at one.");
            return [];
        }

        var found = new List<PluginDescriptor>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var assemblyPath in PluginsDirectories.SelectMany(directory =>
                 {
                     _log.Write("plugins", $"Scanning {directory}");
                     return CandidateAssemblies(directory);
                 }))
        {
            foreach (var descriptor in LoadFrom(assemblyPath))
            {
                if (seen.Add(descriptor.Id))
                    found.Add(descriptor);
                else
                    _log.Write("plugins", $"Ignoring duplicate plugin id '{descriptor.Id}' from {assemblyPath}");
            }
        }

        _log.Write("plugins", $"Discovered {found.Count} plugin(s).");
        return found;
    }

    /// <summary>One subfolder per plugin. Only that subfolder's own DLLs count.</summary>
    private static IEnumerable<string> CandidateAssemblies(string pluginsDirectory)
    {
        foreach (var dir in Directory.EnumerateDirectories(pluginsDirectory))
        {
            // Usually named after the folder. Fall back to scanning so a hand-dropped
            // folder still works.
            var preferred = Path.Combine(dir, Path.GetFileName(dir) + ".dll");
            if (File.Exists(preferred))
            {
                yield return preferred;
                continue;
            }

            foreach (var dll in Directory.EnumerateFiles(dir, "*.dll"))
                yield return dll;
        }
    }

    /// <summary>
    /// Split out because you cannot yield from inside a try/catch. Gives back the types to
    /// scan, or a reason the plugin is unusable.
    /// </summary>
    private (Type[] Types, string? Incompatible) Inspect(string assemblyPath)
    {
        try
        {
            var assembly = new PluginLoadContext(assemblyPath).LoadFromAssemblyPath(assemblyPath);

            // Before touching its types, so an unusable plugin never runs any of its code.
            var problem = ContractCompatibility.Check(
                ContractCompatibility.ReferencedContractVersion(assembly));
            if (problem is not null)
                return ([], problem);

            return (assembly.GetTypes(), null);
        }
        catch (ReflectionTypeLoadException ex)
        {
            return (ex.Types.OfType<Type>().ToArray(), null);
        }
        catch (BadImageFormatException)
        {
            return ([], null); // Native or otherwise non-managed dll sitting in the folder.
        }
        catch (Exception ex)
        {
            _log.Write("plugins", $"Could not load {Path.GetFileName(assemblyPath)}: {ex.Message}");
            return ([], null);
        }
    }

    private IEnumerable<PluginDescriptor> LoadFrom(string assemblyPath)
    {
        var (types, incompatible) = Inspect(assemblyPath);

        if (incompatible is not null)
        {
            _log.Write("plugins", $"{Path.GetFileName(assemblyPath)} is incompatible: {incompatible}");
            yield return PluginDescriptor.Incompatible(assemblyPath, incompatible);
            yield break;
        }

        foreach (var type in types)
        {
            if (!typeof(IMewsPlugin).IsAssignableFrom(type) || type.IsAbstract || type.IsInterface)
                continue;
            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                _log.Write("plugins", $"{type.FullName} implements IMewsPlugin but has no parameterless constructor.");
                continue;
            }

            PluginDescriptor descriptor;
            try
            {
                descriptor = PluginDescriptor.Loaded((IMewsPlugin)Activator.CreateInstance(type)!, assemblyPath);
            }
            catch (Exception ex)
            {
                _log.Write("plugins", $"{type.FullName} failed to construct: {ex.Message}");
                continue;
            }

            _log.Write("plugins", $"Found '{descriptor.DisplayName}' ({descriptor.Id}).");
            yield return descriptor;
        }
    }
}
