using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Sniff;

public partial class SniffView : UserControl
{
    public SniffView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
