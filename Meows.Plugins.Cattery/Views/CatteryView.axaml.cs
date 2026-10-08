using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Meows.Plugins.Cattery.Views;

public partial class CatteryView : UserControl, IDisposable
{
    public CatteryView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
