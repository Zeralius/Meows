namespace Mews.ViewModels;

public sealed class TabViewModel
{
    public TabViewModel(string header, string icon, object content)
    {
        Header = header;
        Icon = icon;
        Content = content;
    }

    public string Header { get; }

    public string Icon { get; }

    public object Content { get; }
}
