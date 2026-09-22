using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Nudge;

public partial class NudgeView : UserControl
{
    public NudgeView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
