using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Yowl;

public partial class YowlView : UserControl
{
    public YowlView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
