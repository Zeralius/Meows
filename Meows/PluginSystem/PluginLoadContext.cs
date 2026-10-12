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
    /// <summary>Always the shell's copy: their types cross between the shell and the plugin.</summary>
    private static readonly string[] MustShare =
    [
        "Meows.Plugins.Abstractions",
        "Avalonia",
    ];

    /// <summary>The shell's copy when it has one at least as new as the plugin asks for.</summary>
    private static readonly string[] PreferShared =
    [
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

        var own = _resolver.ResolveAssemblyToPath(assemblyName);
        var mustShare = MustShare.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        var preferShared = PreferShared.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

        if (!mustShare && !preferShared)
            return own is null ? null : LoadFromAssemblyPath(own);

        // Returning null uses the default context, which is how the types stay identical. But
        // only when the shell has the assembly to give: a name merely starting with System.,
        // Microsoft. or Avalonia used to be sent there whatever it was, so a plugin bringing
        // System.Reactive or Avalonia.Controls.DataGrid, which the shell does not carry, failed
        // to load, and one bringing a newer Microsoft.Data.Sqlite got the shell's older copy and
        // a MissingMethodException later.
        if (own is null)
            return null;

        var shell = ShellVersionOf(assemblyName);
        if (shell is null)
            return LoadFromAssemblyPath(own);
        if (mustShare)
            return null;

        var wanted = assemblyName.Version;
        return wanted is null || shell >= wanted ? null : LoadFromAssemblyPath(own);
    }

    /// <summary>The version the shell would hand out under this name, or null when it has none.</summary>
    private static Version? ShellVersionOf(AssemblyName assemblyName)
    {
        try
        {
            return Default.LoadFromAssemblyName(new AssemblyName(assemblyName.Name!)).GetName().Version;
        }
        catch (Exception)
        {
            return null;
        }
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
    }
}
