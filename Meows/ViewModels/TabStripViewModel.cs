using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using Meows.Plugins.Abstractions;
using Meows.Services;

namespace Meows.ViewModels;

/// <summary>
/// A group's colour as something to paint with.
///
/// A key cannot be bound into {DynamicResource}, so the brush is looked up here instead. That
/// costs the automatic half of following the theme, which is why the strip listens for the theme
/// variant changing and works itself out again: the lookup is then done afresh against whichever
/// dictionary is in force.
/// </summary>
public static class GroupPaint
{
    public static IBrush For(string? colour)
    {
        var key = GroupColours.Resource(colour);
        var app = Application.Current;
        if (app is not null && app.TryGetResource(key, app.ActualThemeVariant, out var found) && found is IBrush brush)
            return brush;

        // No application yet, which is a headless test building a view, or a key the palette has
        // not got. Grey is honest and nothing falls over.
        return Brushes.Gray;
    }
}

/// <summary>
/// Where a drop would put the thing being dragged, relative to the item under the pointer.
/// Drawn as a line down one side of it, which is the only feedback a wrapping strip can give
/// that reads the same on the first row and the third.
/// </summary>
public enum DropHint
{
    None,
    Before,
    After,
}

/// <summary>One colour on a group's menu, so picking one is a click rather than a spelling.</summary>
public sealed class GroupColourChoice(string colour, Action<string> pick)
{
    public string Colour { get; } = colour;

    /// <summary>The colour's own name on the menu, which is the only place it is ever read.</summary>
    public string Name => MeowsText.Current["strip.colour." + colour];

    public IBrush Paint => GroupPaint.For(colour);

    public RelayCommand PickCommand { get; } = new(() => pick(colour));
}

/// <summary>One group a tab could be moved into, as the tab's own menu offers it.</summary>
public sealed class GroupChoice(string key, string name, string colour, Action<string> pick)
{
    public string Key { get; } = key;

    public string Name { get; } = name;

    public IBrush Paint => GroupPaint.For(colour);

    public RelayCommand PickCommand { get; } = new(() => pick(key));
}

/// <summary>
/// Everything that can be done to one tab from its own menu: a step either way along the strip,
/// a move into any other group, a group of its own, and the way back to whatever its plugin
/// declared. Built by the strip each time it is worked out, so the list of groups on offer is
/// the list that exists right now.
/// </summary>
public sealed class TabMenuViewModel : ObservableObject
{
    private string _newGroupName = "";

    public TabMenuViewModel(TabStripViewModel strip, string tabKey, IReadOnlyList<GroupChoice> groups)
    {
        Groups = groups;
        MoveLeftCommand = new RelayCommand(() => strip.MoveTab(tabKey, -1));
        MoveRightCommand = new RelayCommand(() => strip.MoveTab(tabKey, 1));
        ResetCommand = new RelayCommand(() => strip.ResetTab(tabKey));
        NewGroupCommand = new RelayCommand(() => strip.MoveTabToNewGroup(tabKey, NewGroupName));
    }

    /// <summary>Every group but the one it is already in.</summary>
    public IReadOnlyList<GroupChoice> Groups { get; }

    public bool HasGroups => Groups.Count > 0;

    public string NewGroupName
    {
        get => _newGroupName;
        set => SetField(ref _newGroupName, value);
    }

    public RelayCommand MoveLeftCommand { get; }

    public RelayCommand MoveRightCommand { get; }

    public RelayCommand ResetCommand { get; }

    public RelayCommand NewGroupCommand { get; }
}

/// <summary>
/// The chip that stands for a group on the strip: its colour, its name, how many tabs are behind
/// it when it is shut, and the menu for everything that can be done to it.
/// </summary>
public sealed class GroupChipViewModel : ObservableObject
{
    private readonly TabStripViewModel _strip;

    public GroupChipViewModel(TabStripViewModel strip, ArrangedGroup group, string name)
    {
        _strip = strip;
        Group = group;
        Name = name;

        ToggleCommand = new RelayCommand(() => strip.SetCollapsed(Key, !IsCollapsed));
        MoveLeftCommand = new RelayCommand(() => strip.MoveGroup(Key, -1));
        MoveRightCommand = new RelayCommand(() => strip.MoveGroup(Key, 1));
        RenameCommand = new RelayCommand(() => strip.Rename(Key, RenameTo));
        Colours = [.. GroupColours.All.Select(c => new GroupColourChoice(c, colour => strip.Recolour(Key, colour)))];
    }

    public ArrangedGroup Group { get; }

    public string Key => Group.Key;

    /// <summary>What it was renamed to, or the category key read through the string table.</summary>
    public string Name { get; }

    public IBrush Paint => GroupPaint.For(Group.Colour);

    public bool IsCollapsed => Group.IsCollapsed;

    /// <summary>Only on a shut group: the chip says how much is behind it.</summary>
    public string CountText => Group.Tabs.Count.ToString();

    public string Arrow => IsCollapsed ? "▸" : "▾";

    /// <summary>Set while something is being dragged over this chip, and cleared when it leaves.</summary>
    public DropHint Hint
    {
        get => _hint;
        set
        {
            if (!SetField(ref _hint, value))
                return;
            OnPropertyChanged(nameof(HintBefore));
            OnPropertyChanged(nameof(HintAfter));
        }
    }

    public bool HintBefore => _hint == DropHint.Before;

    public bool HintAfter => _hint == DropHint.After;

    private DropHint _hint;

    /// <summary>Bound to the rename box in the chip's menu, so it opens holding the current name.</summary>
    public string RenameTo
    {
        get => _renameTo ??= Name;
        set => SetField(ref _renameTo, value);
    }

    private string? _renameTo;

    public RelayCommand ToggleCommand { get; }

    public RelayCommand MoveLeftCommand { get; }

    public RelayCommand MoveRightCommand { get; }

    public RelayCommand RenameCommand { get; }

    public IReadOnlyList<GroupColourChoice> Colours { get; }

    public string CollapseText => MeowsText.Current[IsCollapsed ? "strip.expand" : "strip.collapse"];
}

/// <summary>
/// The tab strip: Home, then a chip and its tabs for each group.
///
/// The shell showed every tab in one flat row and appended each plugin as it was switched on,
/// which was fine at eight and is not at twenty-three: the strip wrapped onto three rows and
/// took a sixth of the window before anything was shown. Groups are what the Plugins tab has
/// had since 1.3.0, so the strip now reads the same categories, and a plugin that sits under
/// *Disk* on its card sits under *Disk* here without anybody arranging anything.
/// </summary>
public sealed class TabStripViewModel : ObservableObject
{
    private readonly ObservableCollection<TabViewModel> _tabs;
    private readonly ShellPreferences _preferences;
    private readonly Action _save;
    private readonly Func<string, string?> _declaredOf;

    private IReadOnlyList<ArrangedGroup> _arranged = [];

    public TabStripViewModel(
        ObservableCollection<TabViewModel> tabs,
        ShellPreferences preferences,
        Func<string, string?> declaredOf,
        Action save)
    {
        _tabs = tabs;
        _preferences = preferences;
        _declaredOf = declaredOf;
        _save = save;
        _tabs.CollectionChanged += (_, _) => Rebuild();

        // The brushes are looked up rather than bound, so a theme change has to be heard.
        if (Application.Current is { } app)
            app.ActualThemeVariantChanged += (_, _) => Rebuild();

        Rebuild();
    }

    /// <summary>Chips and tabs interleaved, which is what the strip binds to.</summary>
    public ObservableCollection<object> Items { get; } = [];

    /// <summary>Every tab that can be seen right now, in strip order. Ctrl+1 to Ctrl+9 counts these.</summary>
    public List<TabViewModel> Visible { get; private set; } = [];

    /// <summary>The groups as they stand, for the menu that moves a tab into one of them.</summary>
    public IReadOnlyList<ArrangedGroup> Groups => _arranged;

    public event Action? Changed;

    /// <summary>
    /// Works the strip out again from the tabs that exist and what has been remembered. Cheap:
    /// two dozen strings sorted, and it happens when a tab arrives or leaves rather than on a
    /// timer.
    /// </summary>
    public void Rebuild()
    {
        var keys = _tabs.Select(t => t.Key).ToList();
        var declared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tab in _tabs)
        {
            if (tab.Key == TabGroups.HomeKey)
                continue;
            declared[tab.Key] = TabGroups.ShellTabs.Contains(tab.Key)
                ? TabGroups.Shell
                : _declaredOf(tab.Key) ?? TabGroups.Other;
        }

        _arranged = TabGrouping.Arrange(
            keys,
            declared,
            _preferences.TabGroupOf,
            _preferences.TabGroups.Select(g => g.Key).ToList(),
            _preferences.TabOrder,
            _preferences.TabGroups.ToDictionary(g => g.Key, g => g.Colour, StringComparer.OrdinalIgnoreCase),
            _preferences.TabGroups.Where(g => g.Collapsed).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase));

        var byKey = _tabs.ToDictionary(t => t.Key, StringComparer.OrdinalIgnoreCase);

        Items.Clear();
        if (byKey.TryGetValue(TabGroups.HomeKey, out var home))
        {
            home.GroupColour = null;
            Items.Add(home);
        }

        foreach (var group in _arranged)
        {
            Items.Add(new GroupChipViewModel(this, group, NameOf(group.Key)));
            if (group.IsCollapsed)
                continue;
            foreach (var key in group.Tabs)
            {
                if (!byKey.TryGetValue(key, out var tab))
                    continue;
                tab.GroupColour = group.Colour;
                tab.Menu = MenuFor(key, group.Key);
                Items.Add(tab);
            }
        }

        Visible = TabGrouping.Visible(keys, _arranged)
            .Select(k => byKey.GetValueOrDefault(k))
            .Where(t => t is not null)
            .Select(t => t!)
            .ToList();

        Changed?.Invoke();
    }

    /// <summary>The menu for one tab: every group but its own, and the steps either way.</summary>
    private TabMenuViewModel MenuFor(string tabKey, string ownGroup) =>
        new(this, tabKey, _arranged
            .Where(g => !string.Equals(g.Key, ownGroup, StringComparison.OrdinalIgnoreCase))
            .Select(g => new GroupChoice(g.Key, NameOf(g.Key), g.Colour, key => MoveTabToGroup(tabKey, key)))
            .ToList());

    /// <summary>A renamed group as it was renamed, and anything else through the string table.</summary>
    public string NameOf(string key) =>
        Setting(key).Name is { Length: > 0 } named ? named : MeowsText.Current[key];

    private TabGroupSetting Setting(string key)
    {
        var existing = _preferences.TabGroups.FirstOrDefault(g => string.Equals(g.Key, key, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            return existing;

        // The first time a group is touched it gets written down, with a colour nothing else is
        // using. Until then it exists only as whatever the plugins declared.
        var setting = new TabGroupSetting
        {
            Key = key,
            Colour = GroupColours.Next(_preferences.TabGroups.Select(g => g.Colour)),
        };

        // Written in the order the strip shows them, so a group touched for the first time does
        // not jump to the front of the remembered order.
        var at = _arranged.ToList().FindIndex(g => string.Equals(g.Key, key, StringComparison.OrdinalIgnoreCase));
        if (at >= 0 && at <= _preferences.TabGroups.Count)
            _preferences.TabGroups.Insert(at, setting);
        else
            _preferences.TabGroups.Add(setting);

        return setting;
    }

    /// <summary>
    /// Makes sure every group on screen is written down, which is what an order or a colour has
    /// to hang off. Called before anything that reorders, so the remembered list is the whole
    /// list rather than only the groups somebody happened to touch.
    /// </summary>
    private void Settle()
    {
        foreach (var group in _arranged)
            Setting(group.Key);
    }

    public void SetCollapsed(string key, bool collapsed)
    {
        Setting(key).Collapsed = collapsed;
        Save();
    }

    public void Recolour(string key, string colour)
    {
        Setting(key).Colour = colour;
        Save();
    }

    public void Rename(string key, string? name)
    {
        var tidied = name?.Trim();
        // Back to the key's own words rather than an empty chip.
        Setting(key).Name = string.IsNullOrEmpty(tidied) ? null : tidied;
        Save();
    }

    public void MoveGroup(string key, int direction)
    {
        Settle();
        var order = _arranged.Select(g => g.Key).ToList();
        var at = order.FindIndex(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        var to = at + direction;
        if (at < 0 || to < 0 || to >= order.Count)
            return;

        (order[at], order[to]) = (order[to], order[at]);
        _preferences.TabGroups = order
            .Select(k => _preferences.TabGroups.First(g => string.Equals(g.Key, k, StringComparison.OrdinalIgnoreCase)))
            .Concat(_preferences.TabGroups.Where(g => !order.Contains(g.Key, StringComparer.OrdinalIgnoreCase)))
            .ToList();
        Save();
    }

    /// <summary>One step along the strip, crossing into the next group at the edge.</summary>
    public void MoveTab(string tabKey, int direction)
    {
        Settle();
        var (order, landing) = TabGrouping.Nudge(tabKey, direction, _arranged);
        _preferences.TabOrder = order;
        if (landing is not null)
            _preferences.TabGroupOf[tabKey] = landing;
        Save();
    }

    /// <summary>Puts a tab in a named group, which is how the menu moves one across the strip.</summary>
    public void MoveTabToGroup(string tabKey, string groupKey)
    {
        Settle();
        _preferences.TabGroupOf[tabKey] = groupKey;

        // It lands at the end of that group, which is where the eye looks for what just moved.
        var order = _preferences.TabOrder.Where(k => k != tabKey).ToList();
        var group = _arranged.FirstOrDefault(g => string.Equals(g.Key, groupKey, StringComparison.OrdinalIgnoreCase));
        var last = group?.Tabs.LastOrDefault();
        var at = last is null ? -1 : order.IndexOf(last);
        if (at >= 0)
            order.Insert(at + 1, tabKey);
        else
            order.Add(tabKey);

        _preferences.TabOrder = order;
        Save();
    }

    /// <summary>
    /// A group of somebody's own, holding that one tab to start with. The key is made from the
    /// name and kept unique, since it is what everything else is remembered against.
    /// </summary>
    public void MoveTabToNewGroup(string tabKey, string name)
    {
        var tidied = name.Trim();
        if (tidied.Length == 0)
            return;

        var key = "group.own." + new string(tidied.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (key == "group.own.")
            key = "group.own." + Guid.NewGuid().ToString("N")[..6];
        while (_preferences.TabGroups.Any(g => string.Equals(g.Key, key, StringComparison.OrdinalIgnoreCase)))
            key += "x";

        Settle();
        _preferences.TabGroups.Add(new TabGroupSetting
        {
            Key = key,
            Name = tidied,
            Colour = GroupColours.Next(_preferences.TabGroups.Select(g => g.Colour)),
        });
        _preferences.TabGroupOf[tabKey] = key;
        Save();
    }

    /// <summary>
    /// A tab dropped onto another tab: it lands on the side of it the pointer was nearest, and
    /// joins that tab's group if it came from a different one.
    /// </summary>
    public void DropTab(string tabKey, string targetTabKey, bool after)
    {
        if (string.Equals(tabKey, targetTabKey, StringComparison.OrdinalIgnoreCase))
            return;

        Settle();
        var (order, landing) = TabGrouping.Place(_arranged, tabKey, targetTabKey, after);
        _preferences.TabOrder = order;
        if (landing is not null)
            _preferences.TabGroupOf[tabKey] = landing;
        Save();
    }

    /// <summary>
    /// A tab dropped onto a group's chip: it joins that group, at the front when the pointer was
    /// on the chip's leading edge and at the back otherwise. Dropping onto the chip of the group
    /// it is already in is how a tab is sent to either end of its own group.
    /// </summary>
    public void DropTabOnGroup(string tabKey, string groupKey, bool atFront)
    {
        var group = _arranged.FirstOrDefault(g => string.Equals(g.Key, groupKey, StringComparison.OrdinalIgnoreCase));
        if (group is null)
            return;

        var neighbour = atFront ? group.Tabs.FirstOrDefault() : group.Tabs.LastOrDefault();
        if (neighbour is null || string.Equals(neighbour, tabKey, StringComparison.OrdinalIgnoreCase))
        {
            // The group holds nothing else, or only this tab: the move is just the membership.
            Settle();
            _preferences.TabGroupOf[tabKey] = groupKey;
            Save();
            return;
        }

        DropTab(tabKey, neighbour, after: !atFront);
    }

    /// <summary>A group's chip dropped onto another: the groups swap places along the strip.</summary>
    public void DropGroup(string groupKey, string targetGroupKey, bool after)
    {
        if (string.Equals(groupKey, targetGroupKey, StringComparison.OrdinalIgnoreCase))
            return;

        Settle();
        var order = TabGrouping.Reorder(_arranged.Select(g => g.Key).ToList(), groupKey, targetGroupKey, after);
        _preferences.TabGroups = order
            .Select(k => _preferences.TabGroups.First(g => string.Equals(g.Key, k, StringComparison.OrdinalIgnoreCase)))
            .Concat(_preferences.TabGroups.Where(g => !order.Contains(g.Key, StringComparer.OrdinalIgnoreCase)))
            .ToList();
        Save();
    }

    /// <summary>Takes every drop marker back down, however the drag ended.</summary>
    public void ClearHints()
    {
        foreach (var item in Items)
        {
            switch (item)
            {
                case TabViewModel tab:
                    tab.Hint = DropHint.None;
                    break;
                case GroupChipViewModel chip:
                    chip.Hint = DropHint.None;
                    break;
            }
        }
    }

    /// <summary>Back to whatever the plugin said it was, which is the way out of any arrangement.</summary>
    public void ResetTab(string tabKey)
    {
        _preferences.TabGroupOf.Remove(tabKey);
        Save();
    }

    /// <summary>Everything back to what the plugins declare, in the order they were switched on.</summary>
    public void ResetAll()
    {
        _preferences.TabGroups.Clear();
        _preferences.TabGroupOf.Clear();
        _preferences.TabOrder.Clear();
        Save();
    }

    /// <summary>Opens the group a tab is in, so selecting it from the palette lands somewhere visible.</summary>
    public void Reveal(string tabKey)
    {
        var group = _arranged.FirstOrDefault(g => g.Tabs.Contains(tabKey, StringComparer.OrdinalIgnoreCase));
        if (group is { IsCollapsed: true })
            SetCollapsed(group.Key, false);
    }

    /// <summary>Which group a tab is in now, for the menu that offers to move it elsewhere.</summary>
    public string GroupOf(string tabKey) =>
        _arranged.FirstOrDefault(g => g.Tabs.Contains(tabKey, StringComparer.OrdinalIgnoreCase))?.Key ?? TabGroups.Other;

    private void Save()
    {
        _save();
        Rebuild();
    }

    /// <summary>The chips read differently after a language change; the arrangement is unchanged.</summary>
    public void Retranslate() => Rebuild();
}
