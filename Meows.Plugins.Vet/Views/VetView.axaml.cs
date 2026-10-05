using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Meows.Plugins.Vet.Views;

public partial class VetView : UserControl, IDisposable
{
    public VetView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
