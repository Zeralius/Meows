using Meows.Plugins.Abstractions;

namespace Meows.ViewModels;

public sealed class TabViewModel : ObservableObject
{
    private readonly string _header;
    private readonly Func<string>? _name;

    public TabViewModel(string header, string icon, object content)
    {
        _header = header;
        Icon = icon;
        Content = content;
    }

    /// <summary>A plugin's tab, whose name is asked for each time so the feline switch and the language both reach it.</summary>
    public TabViewModel(Func<string> name, string icon, object content)
    {
        _header = "";
        _name = name;
        Icon = icon;
        Content = content;
    }

    /// <summary>
    /// The shell's own tabs go through the string table; a plugin's tab asks the plugin, through
    /// the name switch, so Purrge reads Duplicates the moment the switch is flipped.
    /// </summary>
    public string Header => _name?.Invoke() ?? MeowsText.Current[_header];

    public string Icon { get; }

    public object Content { get; }

    public void Retranslate() => OnPropertyChanged(nameof(Header));
}
