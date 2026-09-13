using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Meows.Plugins.Purr.Views;

public partial class PurrView : UserControl, IDisposable
{
    public PurrView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
