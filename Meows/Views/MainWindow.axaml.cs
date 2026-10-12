using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
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

        // Dragging on the strip: a tab anywhere on it, a chip to reorder the groups.
        _ = new TabStripDrag(this.FindControl<ItemsControl>("Strip")!, () => Model?.Strip);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private MainWindowViewModel? Model => DataContext as MainWindowViewModel;

    private IInputElement? _beforePalette;

    private void Hook()
    {
        if (Model is not { } model)
            return;

        Rescale(model.UiScale);
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.UiScale))
                Rescale(model.UiScale);
        };

        model.Strip.Prompt.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(PromptViewModel.IsOpen))
                return;
            if (model.Strip.Prompt.IsOpen)
                Dispatcher.UIThread.Post(() =>
                {
                    var box = this.FindControl<TextBox>("PromptBox");
                    box?.Focus();
                    box?.SelectAll();
                }, DispatcherPriority.Background);
        };

        model.Palette.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(CommandPaletteViewModel.IsOpen))
                return;

            if (model.Palette.IsOpen)
            {
                // Where the keyboard was, so closing the palette puts it back there rather than
                // nowhere, which for a keyboard or screen reader user means starting over.
                _beforePalette = FocusManager?.GetFocusedElement() as IInputElement;
                Dispatcher.UIThread.Post(() => this.FindControl<TextBox>("PaletteBox")?.Focus(), DispatcherPriority.Background);
                return;
            }

            var back = _beforePalette;
            _beforePalette = null;
            if (back is Visual visual && TopLevel.GetTopLevel(visual) is not null && back.IsEffectivelyVisible)
                Dispatcher.UIThread.Post(() => back.Focus(), DispatcherPriority.Background);
        };
    }

    /// <summary>Everything bigger by the same factor, text, hit targets and spacing alike.</summary>
    private void Rescale(double scale)
    {
        if (this.FindControl<LayoutTransformControl>("Zoom") is { } zoom)
            zoom.LayoutTransform = Math.Abs(scale - 1) < 0.001 ? null : new ScaleTransform(scale, scale);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Model is not { } model)
            return;

        // The name card answers Enter and Escape itself, and nothing else gets keys meanwhile.
        // Not IsDefault and IsCancel on its buttons: those listen on the whole window even while
        // the card is hidden, and would take Escape from every plugin's own confirmation.
        if (model.Strip.Prompt.IsOpen)
        {
            if (e.Key == Key.Escape)
            {
                model.Strip.Prompt.CancelCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && model.Strip.Prompt.OkCommand.CanExecute(null))
            {
                model.Strip.Prompt.OkCommand.Execute(null);
                e.Handled = true;
            }
            return;
        }

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (ctrl && shift && e.Key == Key.K)
        {
            model.OpenPaletteForFrontTab();
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key is Key.K or Key.P)
        {
            model.Palette.Toggle();
            e.Handled = true;
            return;
        }

        // Ctrl+1 to Ctrl+9: the tabs in order. Only when the palette is closed, so a number
        // typed into its box is a number.
        if (ctrl && !model.Palette.IsOpen && e.Key >= Key.D1 && e.Key <= Key.D9)
        {
            model.SelectTabByNumber(e.Key - Key.D0);
            e.Handled = true;
            return;
        }

        // Ctrl+Shift+Left and Right: the tab in front, one place along the strip, which is the
        // keyboard's way to do what dragging does. Not while typing, where the same keys select
        // a word at a time.
        if (ctrl && shift && e.Key is Key.Left or Key.Right && !model.Palette.IsOpen &&
            FocusManager?.GetFocusedElement() is not TextBox)
        {
            model.MoveSelectedTab(e.Key == Key.Left ? -1 : 1);
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
