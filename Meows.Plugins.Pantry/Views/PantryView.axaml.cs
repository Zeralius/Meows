using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Meows.Plugins.Pantry.Views;

public partial class PantryView : UserControl, IDisposable
{
    public PantryView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
