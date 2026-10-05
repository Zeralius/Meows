using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Meows.Plugins.Basket.Views;

public partial class BasketView : UserControl, IDisposable
{
    public BasketView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
