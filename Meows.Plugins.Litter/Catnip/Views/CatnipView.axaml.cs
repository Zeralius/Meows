using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Meows.Plugins.Catnip.ViewModels;

namespace Meows.Plugins.Catnip.Views;

public partial class CatnipView : UserControl, IDisposable
{
    public CatnipView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
