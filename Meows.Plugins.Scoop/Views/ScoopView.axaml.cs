using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Meows.Plugins.Scoop.Views;

public partial class ScoopView : UserControl, IDisposable
{
    public ScoopView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
