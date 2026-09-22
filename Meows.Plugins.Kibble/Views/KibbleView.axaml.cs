using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Meows.Plugins.Kibble.ViewModels;

namespace Meows.Plugins.Kibble.Views;

public partial class KibbleView : UserControl, IDisposable
{
    public KibbleView()
    {
        InitializeComponent();
        var grid = this.FindControl<ListBox>("FileGrid")!;
        grid.SelectionChanged += OnSelectionChanged;
        grid.DoubleTapped += OnGridDoubleTapped;

        // Tunnelled, so the tile under a right click is selected before its menu opens and the
        // menu is about the thing that was clicked rather than whatever was selected before.
        grid.AddHandler(PointerPressedEvent, OnGridPointerPressed, RoutingStrategies.Tunnel);

        // Lambdas rather than overrides, because the visual tree is what matters here. The
        // logical tree attaches first and TopLevel is still null at that point.
        AttachedToVisualTree += (_, _) => HookKeys();
        DetachedFromVisualTree += (_, _) => UnhookKeys();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private KibbleViewModel? Model => DataContext as KibbleViewModel;

    private ListBox? Grid => this.FindControl<ListBox>("FileGrid");

    private TopLevel? _keySource;

    private void KeepFocusOnGrid() =>
        Dispatcher.UIThread.Post(() => Grid?.Focus(), DispatcherPriority.Background);

    /// <summary>
    /// The number keys are listened for on the window rather than on this control, because
    /// focus is the wrong thing to hang them off. Sending removes the tile that had focus, so
    /// a handler that needs focus here works exactly once and then goes quiet until you click
    /// something, which defeats the point of a keyboard workflow. Listening at the window and
    /// checking that this tab is the visible one keeps the keys working no matter where focus
    /// has wandered.
    /// </summary>
    private void HookKeys()
    {
        if (_keySource is not null)
            return;

        _keySource = TopLevel.GetTopLevel(this);
        _keySource?.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        KeepFocusOnGrid();
    }

    private void UnhookKeys()
    {
        _keySource?.RemoveHandler(KeyDownEvent, OnKeyDown);
        _keySource = null;
    }

    /// <summary>The tile a pointer or menu event happened on, if it happened on one.</summary>
    private static IncomingFileViewModel? TileUnder(object? source)
    {
        var control = source as Control;
        while (control is not null)
        {
            if (control.DataContext is IncomingFileViewModel file)
                return file;
            if (control is ListBox)
                break;
            control = control.Parent as Control;
        }

        return null;
    }

    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Grid is not { } grid || !e.GetCurrentPoint(grid).Properties.IsRightButtonPressed)
            return;

        if (TileUnder(e.Source) is not { } file)
            return;

        // Part of a multi selection already: leave the selection alone. Otherwise this tile
        // becomes the selection, so the menu and the preview agree about what is meant.
        if (grid.SelectedItems?.Contains(file) != true)
            grid.SelectedItem = file;
    }

    /// <summary>A double click opens the file the way Explorer would.</summary>
    private void OnGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Model is not { } model || TileUnder(e.Source) is not { } file)
            return;

        model.Open(file);
        e.Handled = true;
    }

    private void OnOpenFile(object? sender, RoutedEventArgs e) =>
        Model?.Open((sender as Control)?.DataContext);

    private void OnOpenFileWith(object? sender, RoutedEventArgs e) =>
        Model?.OpenWith((sender as Control)?.DataContext);

    private void OnRevealFile(object? sender, RoutedEventArgs e) =>
        Model?.Reveal((sender as Control)?.DataContext);

    private void OnCleanWithScruff(object? sender, RoutedEventArgs e) =>
        Model?.CleanWithScruff((sender as Control)?.DataContext);

    /// <summary>
    /// Ctrl and shift ranges are the list control's job, so the view model is simply told what
    /// came out of it rather than tracking clicks itself.
    /// </summary>
    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox list || Model is not { } model)
            return;

        model.SetSelection(list.SelectedItems?.OfType<IncomingFileViewModel>() ?? []);
    }

    /// <summary>
    /// The point of the whole tab: 1 to 9 sends the selected file to that destination, space
    /// skips it. Going through a folder should not need the mouse.
    /// </summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Model is not { } model)
            return;

        // Another plugin's tab may be the one on screen, and it gets these keys too.
        if (!IsEffectivelyVisible)
            return;

        // Never steal keys from a text box or a dropdown. The comic name box is one, so
        // typing a name with digits in it must not fire off nine sends.
        if (e.Source is TextBox or ComboBox)
            return;

        if (e.Key is >= Key.D1 and <= Key.D9)
        {
            model.SendToCommand.Execute(e.Key - Key.D1 + 1);
            KeepFocusOnGrid();
            e.Handled = true;
            return;
        }

        if (e.Key is >= Key.NumPad1 and <= Key.NumPad9)
        {
            model.SendToCommand.Execute(e.Key - Key.NumPad1 + 1);
            KeepFocusOnGrid();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Space)
        {
            model.SkipCommand.Execute(null);
            KeepFocusOnGrid();
            e.Handled = true;
            return;
        }

        // Enter opens the selected file, as it does in Explorer.
        if (e.Key == Key.Enter && model.Selected is not null)
        {
            model.Open(null);
            e.Handled = true;
        }
    }

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
