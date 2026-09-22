using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Meows.Plugins.Portion.ViewModels;

namespace Meows.Plugins.Portion.Views;

public partial class PortionView : UserControl, IDisposable
{
    public PortionView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
