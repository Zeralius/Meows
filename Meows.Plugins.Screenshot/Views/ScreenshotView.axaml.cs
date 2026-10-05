using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Meows.Plugins.Screenshot.Views;

public partial class ScreenshotView : UserControl, IDisposable
{
    public ScreenshotView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
