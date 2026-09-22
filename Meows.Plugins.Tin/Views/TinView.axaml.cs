using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Meows.Plugins.Tin.ViewModels;

namespace Meows.Plugins.Tin.Views;

public partial class TinView : UserControl, IDisposable
{
    public TinView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private TinViewModel? Model => DataContext as TinViewModel;

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
