using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Meows.Plugins.Birdwatch.ViewModels;

namespace Meows.Plugins.Birdwatch.Views;

public partial class BirdwatchView : UserControl
{
    public BirdwatchView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
