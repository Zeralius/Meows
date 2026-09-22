using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Yarn;

public partial class YarnView : UserControl
{
    public YarnView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
