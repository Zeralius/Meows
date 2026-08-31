using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Mews.Views;

public partial class PluginsView : UserControl
{
    public PluginsView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
