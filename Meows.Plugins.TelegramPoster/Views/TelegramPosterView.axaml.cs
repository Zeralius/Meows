using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Meows.Plugins.TelegramPoster.ViewModels;

namespace Meows.Plugins.TelegramPoster.Views;

public partial class TelegramPosterView : UserControl, IDisposable
{
    public TelegramPosterView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
