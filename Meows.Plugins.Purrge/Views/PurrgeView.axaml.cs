using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Meows.Plugins.Purrge.Views;

public partial class PurrgeView : UserControl, IDisposable
{
    public PurrgeView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
