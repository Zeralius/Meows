using Avalonia.Media;
using Meows.Plugins.Abstractions;

namespace Meows.ViewModels;

/// <summary>What the main window shows in a tab's place while the tab is in a window of its own.</summary>
public sealed record PoppedOutNotice(TabViewModel Tab);

public sealed class TabViewModel : ObservableObject
{
    private readonly string _header;
    private readonly Func<string>? _name;
    private bool _isPoppedOut;
    private string? _groupColour;
    private bool _isSelected;
    private DropHint _hint;
    private TabMenuViewModel? _menu;

    public TabViewModel(string header, string icon, object content)
    {
        _header = header;
        _name = null;
        Key = header;
        Icon = icon;
        Content = content;
    }

    /// <summary>A plugin's tab, whose name is asked for each time so the feline switch and the language both reach it.</summary>
    public TabViewModel(string key, Func<string> name, string icon, object content)
    {
        _header = "";
        _name = name;
        Key = key;
        Icon = icon;
        Content = content;
    }

    /// <summary>
    /// What this tab is called when it is remembered: the string key for the shell's own, the
    /// plugin id for a plugin's. Stable across languages and the name switch.
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// The shell's own tabs go through the string table; a plugin's tab asks the plugin, through
    /// the name switch, so Purrge reads Duplicates the moment the switch is flipped.
    /// </summary>
    public string Header => _name?.Invoke() ?? MeowsText.Current[_header];

    public string Icon { get; }

    /// <summary>The tab's real content, wherever it is being shown.</summary>
    public object Content { get; }

    /// <summary>
    /// What the main window puts in the tab: the content, or a notice while the content is in
    /// its own window. A control can only be in one place, so the tab lets go before the window
    /// takes hold.
    /// </summary>
    public object Shown => _isPoppedOut ? new PoppedOutNotice(this) : Content;

    public bool IsPoppedOut
    {
        get => _isPoppedOut;
        set
        {
            if (SetField(ref _isPoppedOut, value))
                OnPropertyChanged(nameof(Shown));
        }
    }

    /// <summary>
    /// The colour of the group this tab is in, so the strip can underline it in that colour.
    /// Null for Home, which is in no group. Set by the strip each time it is worked out rather
    /// than held by the tab, because which group a tab is in is the strip's business.
    /// </summary>
    public string? GroupColour
    {
        get => _groupColour;
        set
        {
            if (!SetField(ref _groupColour, value))
                return;
            OnPropertyChanged(nameof(HasGroup));
            OnPropertyChanged(nameof(Paint));
        }
    }

    public bool HasGroup => _groupColour is not null;

    /// <summary>
    /// Whether this tab belongs to a plugin, and so can be switched off from its own menu. The
    /// shell's own tabs are not optional and do not offer it.
    /// </summary>
    public bool IsPlugin { get; init; }

    public IBrush Paint => GroupPaint.For(_groupColour);

    /// <summary>Set while something is being dragged over this tab, and cleared when it leaves.</summary>
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

    /// <summary>
    /// Whether this is the tab in front. The strip is an ItemsControl rather than a TabControl
    /// now, so nothing marks the selected item for us and the shell says which one it is.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    /// <summary>
    /// What this tab's own menu offers. Set by the strip alongside the colour, for the same
    /// reason: which groups a tab could move into is the strip's business, not the tab's.
    /// </summary>
    public TabMenuViewModel? Menu
    {
        get => _menu;
        set => SetField(ref _menu, value);
    }

    public void Retranslate() => OnPropertyChanged(nameof(Header));
}
