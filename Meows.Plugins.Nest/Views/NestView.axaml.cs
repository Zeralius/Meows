using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Meows.Plugins.Nest.Views;

public partial class NestView : UserControl, IDisposable
{
    public NestView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
