using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Meows.Plugins.TelegramPoster.ViewModels;

namespace Meows.Plugins.TelegramPoster.Views;

public partial class TelegramPosterView : UserControl, IDisposable
{
    public TelegramPosterView()
    {
        InitializeComponent();
        this.FindControl<Button>("ChooseRootButton")!.Click += OnChooseRoot;
        this.FindControl<Button>("BrowseDestinationButton")!.Click += OnBrowseDestination;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>In the view because the folder picker needs a TopLevel.</summary>
    private async void OnChooseRoot(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TelegramPosterViewModel viewModel)
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
            viewModel.SetBotRoot(picked);
    }

    /// <summary>Picks where to clone into. The folder itself does not have to exist.</summary>
    private async void OnBrowseDestination(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TelegramPosterViewModel viewModel)
            return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose where to clone the bot into",
            AllowMultiple = false,
        });

        var picked = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(picked))
            return;

        // Empty folder means clone straight into it. Otherwise nest inside.
        viewModel.Setup.Destination = Directory.Exists(picked) && Directory.EnumerateFileSystemEntries(picked).Any()
            ? Path.Combine(picked, "telegram-posting-bot")
            : picked;
    }

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
