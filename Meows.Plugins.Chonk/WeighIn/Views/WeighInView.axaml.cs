using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Meows.Plugins.WeighIn.Views;

public partial class WeighInView : UserControl, IDisposable
{
    public WeighInView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
