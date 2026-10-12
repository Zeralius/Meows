using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Meows.Plugins.Perch.ViewModels;

namespace Meows.Plugins.Perch.Views;

public partial class PerchView : UserControl, IDisposable
{
    public PerchView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
