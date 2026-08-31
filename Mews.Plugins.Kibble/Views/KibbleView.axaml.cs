using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Mews.Plugins.Kibble.ViewModels;

namespace Mews.Plugins.Kibble.Views;

public partial class KibbleView : UserControl, IDisposable
{
    public KibbleView()
    {
        InitializeComponent();
        this.FindControl<Button>("OpenFolderButton")!.Click += OnOpenFolder;
        this.FindControl<Button>("ChooseBotButton")!.Click += OnChooseBotRoot;
        this.FindControl<ListBox>("FileGrid")!.SelectionChanged += OnSelectionChanged;

        // Tunnel, so a number key reaches us before a focused button treats it as its own.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private KibbleViewModel? Model => DataContext as KibbleViewModel;

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

        // Never steal keys from a text box or a dropdown. The comic name box is one, so
        // typing a name with digits in it must not fire off nine sends.
        if (e.Source is TextBox or ComboBox)
            return;

        if (e.Key is >= Key.D1 and <= Key.D9)
        {
            model.SendToCommand.Execute(e.Key - Key.D1 + 1);
            e.Handled = true;
            return;
        }

        if (e.Key is >= Key.NumPad1 and <= Key.NumPad9)
        {
            model.SendToCommand.Execute(e.Key - Key.NumPad1 + 1);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Space)
        {
            model.SkipCommand.Execute(null);
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
            Title = "Open a folder to sort",
            AllowMultiple = false,
        });

        var picked = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(picked))
        {
            model.LoadFolder(picked);
            Focus();
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
            Title = "Select the telegram-posting-bot folder",
            AllowMultiple = false,
        });

        var picked = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(picked))
            model.SetBotRoot(picked);
    }

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
