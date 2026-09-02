using System.Reflection;
using System.Runtime.Loader;

namespace Meows.Plugins;

/// <summary>
/// Keeps each plugin's private dependencies to itself, but shares the contract and Avalonia
/// with the shell. That sharing is not optional: a plugin with its own Avalonia would hand
/// back a Control of a type we cannot host.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly string[] SharedPrefixes =
    [
        "Meows.Plugins.Abstractions",
        "Avalonia",
        "System.",
        "Microsoft.",
        "netstandard",
        "mscorlib",
    ];

    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginAssemblyPath)
        : base(name: Path.GetFileNameWithoutExtension(pluginAssemblyPath), isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(pluginAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (name is null)
            return null;

        // Returning null uses the default context, which is how the types stay identical.
        if (SharedPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return null;

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
    }
}
