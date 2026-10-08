using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Meows.Plugins.Backlog.Views;

public partial class BacklogView : UserControl, IDisposable
{
    public BacklogView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
