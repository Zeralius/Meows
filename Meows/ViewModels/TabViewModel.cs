using Meows.Plugins.Abstractions;

namespace Meows.ViewModels;

/// <summary>What the main window shows in a tab's place while the tab is in a window of its own.</summary>
public sealed record PoppedOutNotice(TabViewModel Tab);

public sealed class TabViewModel : ObservableObject
{
    private readonly string _header;
    private readonly Func<string>? _name;
    private bool _isPoppedOut;

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

    public void Retranslate() => OnPropertyChanged(nameof(Header));
}
