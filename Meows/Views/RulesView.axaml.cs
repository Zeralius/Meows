using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Meows.Views;

public partial class RulesView : UserControl
{
    public RulesView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
