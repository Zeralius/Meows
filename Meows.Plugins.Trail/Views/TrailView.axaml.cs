using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Meows.Plugins.Trail.Views;

public partial class TrailView : UserControl, IDisposable
{
    public TrailView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
