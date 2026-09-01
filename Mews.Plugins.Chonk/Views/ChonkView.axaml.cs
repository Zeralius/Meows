using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Mews.Plugins.Chonk.ViewModels;

namespace Mews.Plugins.Chonk.Views;

public partial class ChonkView : UserControl, IDisposable
{
    public ChonkView()
    {
        InitializeComponent();
        this.FindControl<Button>("PickFolderButton")!.Click += OnPickFolder;

        var list = this.FindControl<ListBox>("EntryList")!;
        list.DoubleTapped += OnRowActivated;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private ChonkViewModel? Model => DataContext as ChonkViewModel;

    /// <summary>Double click opens a folder, which is what every file manager has trained you to expect.</summary>
    private void OnRowActivated(object? sender, TappedEventArgs e)
    {
        if (Model is { Selected: { CanDrillInto: true } row } model)
            model.OpenCommand.Execute(row);
    }

    private async void OnPickFolder(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model)
            return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Measure which folder",
            AllowMultiple = false,
        });

        var picked = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(picked))
            model.StartScan(picked);
    }

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
