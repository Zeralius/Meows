using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Meows.Plugins.Larder.Views;

public partial class LarderView : UserControl, IDisposable
{
    public LarderView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
