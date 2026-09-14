using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Familiar.ViewModels;

namespace Meows.Plugins.Familiar.Views;

public partial class FamiliarView : UserControl, IDisposable
{
    public FamiliarView()
    {
        InitializeComponent();
        this.FindControl<Button>("AddPicturesButton")!.Click += OnAddPictures;
        this.FindControl<Button>("PickRootButton")!.Click += OnPickRoot;
        this.FindControl<Button>("PickExportButton")!.Click += OnPickExport;

        // Enter in the name box renames, the way a name box in a file manager does.
        var name = this.FindControl<TextBox>("NameBox")!;
        name.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Model is { } model && model.RenameCommand.CanExecute(null))
            {
                model.RenameCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private FamiliarViewModel? Model => DataContext as FamiliarViewModel;

    private async void OnAddPictures(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;

        var picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = MeowsText.Current["familiar.dialog.pictures"],
            AllowMultiple = true,
            FileTypeFilter = [FilePickerFileTypes.ImageAll, FilePickerFileTypes.All],
        });

        model.AddPictures(picked.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Select(p => p!));
    }

    private async void OnPickRoot(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model)
            return;
        if (await PickFolder(MeowsText.Current["familiar.dialog.root"]) is { } folder)
            model.SetRoot(folder);
    }

    private async void OnPickExport(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model)
            return;
        if (await PickFolder(MeowsText.Current["familiar.dialog.export"]) is { } folder)
            model.SetExportRoot(folder);
    }

    private async Task<string?> PickFolder(string title)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return null;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
