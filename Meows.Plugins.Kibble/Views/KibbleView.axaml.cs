using Meows.Plugins.Abstractions;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Meows.Plugins.Kibble.ViewModels;

namespace Meows.Plugins.Kibble.Views;

public partial class KibbleView : UserControl, IDisposable
{
    public KibbleView()
    {
        InitializeComponent();
        this.FindControl<Button>("OpenFolderButton")!.Click += OnOpenFolder;
        this.FindControl<Button>("ChooseBotButton")!.Click += OnChooseBotRoot;
        this.FindControl<ListBox>("FileGrid")!.SelectionChanged += OnSelectionChanged;

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
        }
    }

    private async void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model)
            return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = MeowsText.Current["kibble.dialog.openfolder"],
            AllowMultiple = false,
        });

        var picked = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(picked))
        {
            model.LoadFolder(picked);
            KeepFocusOnGrid();
        }
    }

    private async void OnChooseBotRoot(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model)
            return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = MeowsText.Current["kibble.dialog.botfolder"],
            AllowMultiple = false,
        });

        var picked = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(picked))
            model.SetBotRoot(picked);
    }

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
