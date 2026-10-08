using Meows.Plugins.Abstractions;

namespace Meows.ViewModels;

/// <summary>
/// The filter on the Plugins tab, as arithmetic: which chips there are and what each one lets
/// through. Pure, so the counts a test asserts are the counts on the chips.
/// </summary>
public static class PluginFilter
{
    /// <summary>Every topic, in the order the chips run. All is not one of them; it is the absence of one.</summary>
    public static IReadOnlyList<PluginTopics> Topics { get; } =
        Enum.GetValues<PluginTopics>().Where(t => t != PluginTopics.None).ToList();

    /// <summary>Whether a plugin about <paramref name="topics"/> shows under <paramref name="selected"/>; null is All.</summary>
    public static bool Shows(PluginTopics topics, PluginTopics? selected) =>
        selected is not { } topic || topics.HasFlag(topic);

    /// <summary>
    /// The chips for what is installed: All with everything, then each topic at least one plugin
    /// names, with how many. A topic nobody installed has no chip, so a fresh Meows with three
    /// plugins does not offer seven filters that each show nothing.
    /// </summary>
    public static IReadOnlyList<(PluginTopics? Topic, int Count)> Chips(IReadOnlyCollection<PluginTopics> installed) =>
        Topics
            .Select(topic => ((PluginTopics?)topic, installed.Count(t => t.HasFlag(topic))))
            .Where(chip => chip.Item2 > 0)
            .Prepend((null, installed.Count))
            .ToList();

    /// <summary>The topic a kept name stands for, or null (All) for nothing, junk, or a flag combination.</summary>
    public static PluginTopics? Parse(string? kept) =>
        Enum.TryParse<PluginTopics>(kept, ignoreCase: true, out var topic) && Topics.Contains(topic) ? topic : null;
}

/// <summary>
/// One chip above the plugin cards. Pressing it picks it, and the tab builds the chips again;
/// pressing the one already picked leaves it picked, since "no filter" is the All chip rather
/// than nothing being pressed.
/// </summary>
public sealed class PluginFilterViewModel(PluginTopics? topic, int count, bool isSelected, Action<PluginTopics?> pick)
    : ObservableObject
{
    private readonly bool _isSelected = isSelected;

    public PluginTopics? Topic { get; } = topic;

    public int Count { get; } = count;

    public string Name => MeowsText.Current[Topic is { } t ? "plugins.topic." + t.ToString().ToLowerInvariant() : "plugins.topic.all"];

    public string Text => $"{Name} {Count}";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (value && !_isSelected)
                pick(Topic);
            else
                OnPropertyChanged();
        }
    }
}
