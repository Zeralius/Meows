using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Tin.ViewModels;

namespace Meows.Plugins.Tin.Views;

public partial class TinView : UserControl, IDisposable
{
    public TinView()
    {
        InitializeComponent();
        this.FindControl<Button>("PickFolderButton")!.Click += OnPickFolder;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private TinViewModel? Model => DataContext as TinViewModel;

    private async void OnPickFolder(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model)
            return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = MeowsText.Current["tin.dialog.folder"],
            AllowMultiple = false,
        });

        var picked = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(picked))
            model.SetFolder(picked);
    }

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
