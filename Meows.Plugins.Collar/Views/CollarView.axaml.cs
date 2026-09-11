using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Collar.ViewModels;

namespace Meows.Plugins.Collar.Views;

public partial class CollarView : UserControl, IDisposable
{
    public CollarView()
    {
        InitializeComponent();
        this.FindControl<Button>("AttachButton")!.Click += OnPickFile;

        // A list that has to be typed into is a list nobody keeps up, so a receipt dropped on the
        // tab fills in most of an entry by itself.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private CollarViewModel? Model => DataContext as CollarViewModel;

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

        foreach (var item in files)
        {
            // A folder is not a receipt, and the local path is null for anything that only exists
            // inside another application.
            if (item is not IStorageFile file || file.TryGetLocalPath() is not { } path)
                continue;

            model.AddFromFile(path);
        }

        e.Handled = true;
    }

    private async void OnPickFile(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model)
            return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return;

        var picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = MeowsText.Current["collar.dialog.file"],
            AllowMultiple = false,
        });

        var path = picked.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
            model.AddFromFile(path);
    }

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
