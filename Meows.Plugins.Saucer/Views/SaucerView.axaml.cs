using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Meows.Plugins.Saucer.ViewModels;

namespace Meows.Plugins.Saucer.Views;

public partial class SaucerView : UserControl, IDisposable
{
    public SaucerView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private SaucerViewModel? Model => DataContext as SaucerViewModel;

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
