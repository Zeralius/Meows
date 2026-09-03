using Meows.Plugins.Abstractions;

namespace Meows.ViewModels;

public sealed class TabViewModel : ObservableObject
{
    private readonly string _header;

    public TabViewModel(string header, string icon, object content)
    {
        _header = header;
        Icon = icon;
        Content = content;
    }

    /// <summary>
    /// Run through the string table, so the shell's own tabs are translated and a plugin's name
    /// comes back exactly as it gave it. Plugin names are names rather than words, so none of
    /// them are keys and none of them change.
    /// </summary>
    public string Header => MeowsText.Current[_header];

    public string Icon { get; }

    public object Content { get; }

    public void Retranslate() => OnPropertyChanged(nameof(Header));
}
