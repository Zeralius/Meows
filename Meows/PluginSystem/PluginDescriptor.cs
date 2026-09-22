using Meows.Plugins.Abstractions;

namespace Meows.Plugins;

/// <summary>
/// Something we found on disk: either a working plugin or a refusal with a reason. An
/// incompatible one is never constructed, so it gets no chance to fail confusingly.
/// </summary>
public sealed record PluginDescriptor
{
    private PluginDescriptor(string assemblyPath) => AssemblyPath = assemblyPath;

    /// <summary>Null when incompatible. Nothing of it was constructed.</summary>
    public IMeowsPlugin? Plugin { get; private init; }

    public string AssemblyPath { get; }

    public string Id { get; private init; } = "";

    public string DisplayName { get; private init; } = "";

    /// <summary>The feline or the plain name, whichever the switch says. The feline one is the identity.</summary>
    public string Name => Plugin is null ? DisplayName : PluginNames.For(Plugin);

    /// <summary>The name that is not showing, or empty when the plugin has only the one.</summary>
    public string OtherName => Plugin is null || Plugin.PlainName == Plugin.DisplayName ? "" : PluginNames.Other(Plugin);

    public string Description { get; private init; } = "";

    public string Icon { get; private init; } = "●";

    /// <summary>What the plugin called its group, or null if it did not say.</summary>
    public string? Category { get; private init; }

    /// <summary>Why we would not load it, phrased for whoever is reading the card.</summary>
    public string? IncompatibleReason { get; private init; }

    public bool IsCompatible => IncompatibleReason is null && Plugin is not null;

    public string Origin => Path.GetFileName(Path.GetDirectoryName(AssemblyPath)) ?? AssemblyPath;

    /// <summary>The folder the plugin was loaded from, which is what an uninstall takes away.</summary>
    public string Folder => Path.GetDirectoryName(AssemblyPath) ?? AssemblyPath;

    /// <summary>Version, author, homepage and the installer's note, whichever of those exist.</summary>
    public PluginProvenance Provenance { get; private init; } = PluginProvenance.None;

    public static PluginDescriptor Loaded(IMeowsPlugin plugin, string assemblyPath, PluginProvenance? provenance = null) =>
        new(assemblyPath)
        {
            Plugin = plugin,
            Id = plugin.Id,
            DisplayName = plugin.DisplayName,
            Description = plugin.Description,
            Icon = plugin.Icon ?? "●",
            Category = Tidy(plugin.Category),
            Provenance = provenance ?? PluginProvenance.None,
        };

    private static string? Tidy(string? category) =>
        string.IsNullOrWhiteSpace(category) ? null : category.Trim();

    /// <summary>
    /// Only the file name to go on, since constructing it is the thing we are refusing to do.
    /// </summary>
    public static PluginDescriptor Incompatible(string assemblyPath, string reason, PluginProvenance? provenance = null) =>
        new(assemblyPath)
        {
            Id = "file:" + Path.GetFileNameWithoutExtension(assemblyPath),
            DisplayName = Path.GetFileNameWithoutExtension(assemblyPath),
            Description = "plugins.unloadable",
            Icon = "⛔",
            IncompatibleReason = reason,
            Provenance = provenance ?? PluginProvenance.None,
        };
}
