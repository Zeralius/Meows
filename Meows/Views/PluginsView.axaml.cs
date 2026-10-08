using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Meows.Plugins.Abstractions;
using Meows.ViewModels;

namespace Meows.Views;

public partial class PluginsView : UserControl
{
    public PluginsView()
    {
        InitializeComponent();
        this.FindControl<Button>("InstallButton")!.Click += OnInstall;
        this.FindControl<Button>("SharedButton")!.Click += OnInstallShared;

        // The zip can be dropped on the tab as well as picked; it is the same install.
        // A shared-settings zip ends in .meows-share.zip and goes to the plugin that kept it.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel model || e.DataTransfer.TryGetFiles() is not { } files)
            return;

        foreach (var item in files)
        {
            if (item is IStorageFile file && file.TryGetLocalPath() is { } path &&
                path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                if (path.EndsWith(".meows-share.zip", StringComparison.OrdinalIgnoreCase))
                    model.InstallShared(path);
                else
                    model.InstallPlugin(path);
            }
        }

        e.Handled = true;
    }

    private async void OnInstall(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel model || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = MeowsText.Current["plugins.install"],
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(MeowsText.Current["plugins.install.picker"]) { Patterns = ["*.zip"] }],
        });

        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
            model.InstallPlugin(path);
    }

    private async void OnInstallShared(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel model || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = MeowsText.Current["plugins.shared.install"],
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(MeowsText.Current["plugins.shared.picker"]) { Patterns = ["*.meows-share.zip"] }],
        });

        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
            model.InstallShared(path);
    }
}
