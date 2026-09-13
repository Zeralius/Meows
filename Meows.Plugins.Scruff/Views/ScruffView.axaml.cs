using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Scruff.ViewModels;

namespace Meows.Plugins.Scruff.Views;

public partial class ScruffView : UserControl, IDisposable
{
    public ScruffView()
    {
        InitializeComponent();
        this.FindControl<Button>("PickOutputButton")!.Click += OnPickOutput;
        this.FindControl<Button>("AddFilesButton")!.Click += OnAddFiles;
        this.FindControl<Button>("AddFolderButton")!.Click += OnAddFolder;

        // Dropping a folder or a handful of files on the tab is how most of them will arrive.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        DataContextChanged += (_, _) =>
        {
            // The clipboard belongs to the window, so the view lends it to the view model.
            if (Model is { } model)
                model.CopyText = text => _ = CopyAsync(text);
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private ScruffViewModel? Model => DataContext as ScruffViewModel;

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (Model is not { } model)
            return;

        var files = e.DataTransfer.TryGetFiles();
        if (files is null)
            return;

        var paths = files
            .Select(item => item.TryGetLocalPath())
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(path => path!)
            .ToList();

        model.AddPaths(paths);
        e.Handled = true;
    }

    private async void OnAddFiles(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model)
            return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return;

        var start = model.LastFolder is { } last ? await storage.TryGetFolderFromPathAsync(last) : null;

        var picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = MeowsText.Current["scruff.dialog.files"],
            AllowMultiple = true,
            SuggestedStartLocation = start,
            FileTypeFilter = [FilePickerFileTypes.ImageAll, FilePickerFileTypes.All],
        });

        model.AddPaths(picked.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Select(p => p!));
    }

    private async void OnAddFolder(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model)
            return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return;

        var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = MeowsText.Current["scruff.dialog.folder"],
            AllowMultiple = false,
        });

        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path)
            model.AddPaths([path]);
    }

    private async void OnPickOutput(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model)
            return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return;

        var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = MeowsText.Current["scruff.dialog.output"],
            AllowMultiple = false,
        });

        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path)
            model.SetOutputFolder(path);
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
