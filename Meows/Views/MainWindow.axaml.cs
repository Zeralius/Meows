using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Meows.ViewModels;

namespace Meows.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Tunnelled at the window, so Ctrl+K opens the palette wherever focus is, and the
        // palette's own keys are answered before a list or a text box gets them.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => Hook();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private MainWindowViewModel? Model => DataContext as MainWindowViewModel;

    private void Hook()
    {
        if (Model is not { } model)
            return;

        model.Palette.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CommandPaletteViewModel.IsOpen) && model.Palette.IsOpen)
                Dispatcher.UIThread.Post(() => this.FindControl<TextBox>("PaletteBox")?.Focus(), DispatcherPriority.Background);
        };
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Model is not { } model)
            return;

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (ctrl && e.Key is Key.K or Key.P)
        {
            model.Palette.Toggle();
            e.Handled = true;
            return;
        }

        if (!model.Palette.IsOpen)
            return;

        switch (e.Key)
        {
            case Key.Escape:
                model.Palette.IsOpen = false;
                e.Handled = true;
                break;
            case Key.Down:
                model.Palette.MoveSelection(+1);
                e.Handled = true;
                break;
            case Key.Up:
                model.Palette.MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                model.Palette.RunSelected();
                e.Handled = true;
                break;
        }
    }

    private void OnPaletteVeilPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Model is { } model)
            model.Palette.IsOpen = false;
    }

    private void OnPaletteRun(object? sender, TappedEventArgs e) => Model?.Palette.RunSelected();
}
