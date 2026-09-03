using System.Collections.ObjectModel;
using Meows.Plugins.Abstractions;

namespace Meows.ViewModels;

/// <summary>
/// One heading on the Plugins tab, with the cards that sit under it.
///
/// The grouping is the plugin's own word for itself rather than anything the shell knows. Eight
/// plugins is enough that one flat list stopped being readable, and a shell that hardcoded which
/// plugin belongs where would be wrong the moment somebody dropped their own into the folder.
/// </summary>
public sealed class PluginGroupViewModel(string name, IEnumerable<PluginEntryViewModel> entries)
    : ObservableObject
{
    /// <summary>Where anything that did not name a group ends up. Always shown last.</summary>
    public const string Ungrouped = "group.other";

    /// <summary>
    /// Grouping is done on the raw category so two plugins that name the same group stay together
    /// in every language. Only the heading itself goes through the string table.
    /// </summary>
    public string Key { get; } = name;

    public string Name => MeowsText.Current[Key];

    public ObservableCollection<PluginEntryViewModel> Entries { get; } = new(entries);

    public string CountText => Entries.Count == 1
        ? MeowsText.Current["plugins.count.one"]
        : MeowsText.Current.Format("plugins.count.many", Entries.Count);

    /// <summary>
    /// Sorts cards under their headings. Groups run alphabetically by the heading as shown, so
    /// the order does not depend on which plugin happened to load first, and it follows the
    /// language rather than sitting in English order. The one holding everything that named no
    /// group is always last however it happens to sort.
    ///
    /// Matching ignores case, so a plugin that writes "posting bot" still lands beside one that
    /// wrote "Posting bot". The first spelling seen names the group.
    /// </summary>
    public static List<PluginGroupViewModel> Arrange(IEnumerable<PluginEntryViewModel> plugins) =>
        plugins
            .GroupBy(p => p.Descriptor.Category ?? Ungrouped, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key.Equals(Ungrouped, StringComparison.OrdinalIgnoreCase))
            .ThenBy(g => MeowsText.Current[g.Key], StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new PluginGroupViewModel(
                g.Key,
                g.OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)))
            .ToList();
}
