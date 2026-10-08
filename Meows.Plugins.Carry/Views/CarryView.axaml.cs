using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Meows.Plugins.Carry.Views;

public partial class CarryView : UserControl, IDisposable
{
    public CarryView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
