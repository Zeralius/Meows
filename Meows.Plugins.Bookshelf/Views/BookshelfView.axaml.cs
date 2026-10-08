using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Meows.Plugins.Bookshelf.Views;

public partial class BookshelfView : UserControl, IDisposable
{
    public BookshelfView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
