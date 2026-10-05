using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Meows.Plugins.Naptime.Views;

public partial class NaptimeView : UserControl, IDisposable
{
    public NaptimeView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
