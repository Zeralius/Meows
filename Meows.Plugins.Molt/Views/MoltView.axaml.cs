using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Meows.Plugins.Molt.ViewModels;

namespace Meows.Plugins.Molt.Views;

public partial class MoltView : UserControl, IDisposable
{
    private TopLevel? _keySource;

    public MoltView()
    {
        InitializeComponent();

        AttachedToVisualTree += (_, _) => HookKeys();
        DetachedFromVisualTree += (_, _) => UnhookKeys();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private MoltViewModel? Model => DataContext as MoltViewModel;

    private void HookKeys()
    {
        if (_keySource is not null)
            return;

        _keySource = TopLevel.GetTopLevel(this);
        _keySource?.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private void UnhookKeys()
    {
        _keySource?.RemoveHandler(KeyDownEvent, OnKeyDown);
        _keySource = null;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsEffectivelyVisible || Model is not { IsAsking: true } model || e.Key != Key.Escape)
            return;

        model.CancelShedCommand.Execute(null);
        e.Handled = true;
    }

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
