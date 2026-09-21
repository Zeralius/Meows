using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Rehome.ViewModels;

namespace Meows.Plugins.Rehome.Views;

public partial class RehomeView : UserControl, IDisposable
{
    public RehomeView()
    {
        InitializeComponent();
        this.FindControl<Button>("PickDestinationButton")!.Click += OnPickDestination;
        this.FindControl<Button>("OpenManifestButton")!.Click += OnOpenManifest;
        DataContextChanged += (_, _) =>
        {
            if (DataContext is RehomeViewModel model)
                model.CopyText = text => _ = CopyAsync(text);
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnPickDestination(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RehomeViewModel model || await PickFolderAsync(MeowsText.Current["rehome.dialog.destination"]) is not { } folder)
            return;
        model.SetDestination(folder);
    }

    private async void OnOpenManifest(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RehomeViewModel model || await PickFolderAsync(MeowsText.Current["rehome.dialog.manifest"]) is not { } folder)
            return;
        model.LoadRehomeFolder(folder);
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return null;
        var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    private async Task CopyAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null || text.Length == 0)
            return;
        try
        {
            await clipboard.SetTextAsync(text);
        }
        catch (Exception)
        {
            // Another program has the clipboard open. Pressing the button again is the fix.
        }
    }

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
