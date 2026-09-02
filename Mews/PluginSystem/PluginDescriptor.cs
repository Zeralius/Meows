using Mews.Plugins.Abstractions;

namespace Mews.Plugins;

/// <summary>
/// Something we found on disk: either a working plugin or a refusal with a reason. An
/// incompatible one is never constructed, so it gets no chance to fail confusingly.
/// </summary>
public sealed record PluginDescriptor
{
    private PluginDescriptor(string assemblyPath) => AssemblyPath = assemblyPath;

    /// <summary>Null when incompatible. Nothing of it was constructed.</summary>
    public IMewsPlugin? Plugin { get; private init; }

    public string AssemblyPath { get; }

    public string Id { get; private init; } = "";

    public string DisplayName { get; private init; } = "";

    public string Description { get; private init; } = "";

    public string Icon { get; private init; } = "●";

    /// <summary>What the plugin called its group, or null if it did not say.</summary>
    public string? Category { get; private init; }

    /// <summary>Why we would not load it, phrased for whoever is reading the card.</summary>
    public string? IncompatibleReason { get; private init; }

    public bool IsCompatible => IncompatibleReason is null && Plugin is not null;

    public string Origin => Path.GetFileName(Path.GetDirectoryName(AssemblyPath)) ?? AssemblyPath;

    public static PluginDescriptor Loaded(IMewsPlugin plugin, string assemblyPath) =>
        new(assemblyPath)
        {
            Plugin = plugin,
            Id = plugin.Id,
            DisplayName = plugin.DisplayName,
            Description = plugin.Description,
            Icon = plugin.Icon ?? "●",
            Category = Tidy(plugin.Category),
        };

    private static string? Tidy(string? category) =>
        string.IsNullOrWhiteSpace(category) ? null : category.Trim();

    /// <summary>
    /// Only the file name to go on, since constructing it is the thing we are refusing to do.
    /// </summary>
    public static PluginDescriptor Incompatible(string assemblyPath, string reason) =>
        new(assemblyPath)
        {
            Id = "file:" + Path.GetFileNameWithoutExtension(assemblyPath),
            DisplayName = Path.GetFileNameWithoutExtension(assemblyPath),
            Description = "This plugin cannot be loaded by this version of Mews.",
            Icon = "⛔",
            IncompatibleReason = reason,
        };
}
