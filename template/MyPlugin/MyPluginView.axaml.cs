using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MyPlugin;

public partial class MyPluginView : UserControl
{
    public MyPluginView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
