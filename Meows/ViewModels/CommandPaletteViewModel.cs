using System.Collections.ObjectModel;
using Meows.Plugins.Abstractions;

namespace Meows.ViewModels;

/// <summary>One thing the palette can do, with the words to find it by.</summary>
public sealed class PaletteItem(string glyph, string title, string subtitle, Action run, int weight = 0)
{
    public string Glyph { get; } = glyph;

    public string Title { get; } = title;

    public string Subtitle { get; } = subtitle;

    public bool HasSubtitle => Subtitle.Length > 0;

    /// <summary>Higher sorts earlier among equal matches. Tabs over settings over history.</summary>
    public int Weight { get; } = weight;

    public Action Run { get; } = run;

    /// <summary>Offered when the query starts with <c>&gt;</c>: something to do, not somewhere to go.</summary>
    public bool IsCommand { get; init; }

    /// <summary>Only under <c>&gt;</c>, so the plain list stays the plain list.</summary>
    public bool CommandOnly { get; init; }
}

/// <summary>
/// One place something typed can go: a plugin's action, with the words to find it by. Picking
/// one does not run anything yet; it asks what the action should take.
/// </summary>
public sealed record AddTarget(string PluginId, string PluginName, string Glyph, string ActionId, string Label, string? Hint);

/// <summary>
/// The command palette: Ctrl+K, type, Enter.
///
/// Everything the window can do from one box, which is quicker than the tabs once there are
/// fourteen of them: open or jump to a plugin by either of its names, flip a setting, find a
/// line of history and land on the file. The items come from whoever owns them, handed in as
/// a function so the list is built fresh each time it opens and never goes stale.
/// </summary>
public sealed class CommandPaletteViewModel : ObservableObject
{
    private readonly Func<IEnumerable<PaletteItem>> _fixed;
    private readonly Func<string, IEnumerable<PaletteItem>> _searched;
    private readonly Func<IEnumerable<AddTarget>> _addTargets;
    private readonly Func<AddTarget, string, Task> _add;
    private bool _isOpen;
    private string _query = "";
    private PaletteItem? _selected;
    private string? _scopeKey;
    private string _scopeName = "";
    private AddTarget? _addTarget;

    public CommandPaletteViewModel(
        Func<IEnumerable<PaletteItem>> fixedItems,
        Func<string, IEnumerable<PaletteItem>> searchedItems,
        Func<IEnumerable<AddTarget>>? addTargets = null,
        Func<AddTarget, string, Task>? add = null)
    {
        _fixed = fixedItems;
        _searched = searchedItems;
        _addTargets = addTargets ?? (() => []);
        _add = add ?? ((_, _) => Task.CompletedTask);
        RunCommand = new RelayCommand(RunSelected);
        CloseCommand = new RelayCommand(() => IsOpen = false);
    }

    public ObservableCollection<PaletteItem> Items { get; } = [];

    public RelayCommand RunCommand { get; }

    public RelayCommand CloseCommand { get; }

    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (!SetField(ref _isOpen, value))
                return;
            if (value)
            {
                Query = "";
                Rebuild();
            }
            else
            {
                _addTarget = null;
            }
        }
    }

    public string Query
    {
        get => _query;
        set
        {
            if (SetField(ref _query, value ?? ""))
                Rebuild();
        }
    }

    public PaletteItem? Selected
    {
        get => _selected;
        set => SetField(ref _selected, value);
    }

    public bool IsEmpty => Items.Count == 0;

    /// <summary>
    /// Ctrl+Shift+K: the tab in front and nothing else. The key of that tab, for whoever answers
    /// the search, and its name for the box; null is the whole window as usual.
    /// </summary>
    public string? ScopeKey => _scopeKey;

    public bool IsScoped => _scopeKey is not null;

    /// <summary>What the box says before anything is typed: the usual hint, the tab being searched, or what adding takes.</summary>
    public string Hint => _addTarget is { } add
        ? MeowsText.Current.Format("palette.add.hint", add.PluginName, add.Label)
        : _scopeKey is null
            ? MeowsText.Current["palette.hint"]
            : MeowsText.Current.Format("palette.hint.scoped", _scopeName);

    public void Open()
    {
        _scopeKey = null;
        _scopeName = "";
        _addTarget = null;
        OnPropertyChanged(nameof(Hint));
        OnPropertyChanged(nameof(IsScoped));
        IsOpen = true;
    }

    /// <summary>Ctrl+K then <c>+</c>: adding, starting at picking what takes it.</summary>
    public void OpenAdd()
    {
        Open();
        Query = "+";
    }

    public void OpenScoped(string tabKey, string tabName)
    {
        _scopeKey = tabKey;
        _scopeName = tabName;
        OnPropertyChanged(nameof(Hint));
        OnPropertyChanged(nameof(IsScoped));
        if (IsOpen)
            Rebuild();
        else
            IsOpen = true;
    }

    public void Toggle()
    {
        if (IsOpen)
            IsOpen = false;
        else
            Open();
    }

    /// <summary>Whether a query is asking for commands, and the words after the mark.</summary>
    public static bool IsCommandQuery(string query, out string rest)
    {
        var trimmed = query.TrimStart();
        if (trimmed.StartsWith('>'))
        {
            rest = trimmed[1..].Trim();
            return true;
        }
        rest = trimmed;
        return false;
    }

    /// <summary>Whether a query is adding something, and the words after the mark.</summary>
    public static bool IsAddQuery(string query, out string rest)
    {
        var trimmed = query.TrimStart();
        if (trimmed.StartsWith('+'))
        {
            rest = trimmed[1..].Trim();
            return true;
        }
        rest = trimmed;
        return false;
    }

    /// <summary>Whether the box is asking what an action should take, rather than listing.</summary>
    public bool IsAdding => _addTarget is not null;

    public void MoveSelection(int delta)
    {
        if (Items.Count == 0)
            return;
        var index = Selected is null ? -1 : Items.IndexOf(Selected);
        index = Math.Clamp(index + delta, 0, Items.Count - 1);
        Selected = Items[index];
    }

    public void RunSelected()
    {
        if (Selected is not { } item)
            return;

        IsOpen = false;
        item.Run();
    }

    /// <summary>
    /// Every word typed has to appear somewhere in the title or the subtitle, in any order. Items
    /// whose title starts with the first word come first, then by weight, then by title.
    /// </summary>
    public static IReadOnlyList<PaletteItem> Rank(IEnumerable<PaletteItem> items, string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
            return items.OrderByDescending(i => i.Weight).ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase).ToList();

        return items
            .Where(i => words.All(w =>
                i.Title.Contains(w, StringComparison.CurrentCultureIgnoreCase) ||
                i.Subtitle.Contains(w, StringComparison.CurrentCultureIgnoreCase)))
            .OrderByDescending(i => i.Title.StartsWith(words[0], StringComparison.CurrentCultureIgnoreCase))
            .ThenByDescending(i => i.Weight)
            .ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private void Rebuild()
    {
        var query = _query.Trim();
        List<PaletteItem> all;

        if (_addTarget is { } adding)
        {
            // Asking what an action should take: the one thing Enter does, or nothing on empty.
            all = query.Length == 0
                ? []
                : [new PaletteItem(adding.Glyph,
                    MeowsText.Current.Format("palette.add.do", query, adding.PluginName),
                    adding.Hint ?? "",
                    () =>
                    {
                        var target = adding;
                        var text = query;
                        _ = _add(target, text);
                    })];
        }
        else if (_scopeKey is not null)
        {
            // One tab: nothing fixed, only what that tab answers, from two letters on.
            all = query.Length >= 2 ? _searched(query).ToList() : [];
        }
        else if (IsAddQuery(query, out var rest))
        {
            all = _addTargets()
                .Select(t => new PaletteItem(t.Glyph, $"{t.PluginName}: {t.Label}", t.Hint ?? "", () => EnterAddTarget(t)))
                .ToList();
            query = rest;
        }
        else if (IsCommandQuery(query, out var commandRest))
        {
            all = _fixed().Where(i => i.IsCommand).ToList();
            query = commandRest;
        }
        else
        {
            all = _fixed().Where(i => !i.CommandOnly).ToList();
            if (query.Length >= 2)
                all.AddRange(_searched(query));
        }

        Items.Clear();
        foreach (var item in Rank(all, query).Take(40))
            Items.Add(item);

        Selected = Items.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>An add target picked: the box now asks what it should take.</summary>
    private void EnterAddTarget(AddTarget target)
    {
        _addTarget = target;
        OnPropertyChanged(nameof(Hint));
        OnPropertyChanged(nameof(IsAdding));
        Query = "";
        // RunSelected closes the box before running the item; adding stays open for the words.
        IsOpen = true;
    }
}
