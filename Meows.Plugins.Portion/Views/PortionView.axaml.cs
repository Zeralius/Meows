using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Portion.ViewModels;

namespace Meows.Plugins.Portion.Views;

public partial class PortionView : UserControl, IDisposable
{
    public PortionView()
    {
        InitializeComponent();
        this.FindControl<Button>("ChooseBotButton")!.Click += OnChooseBotRoot;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnChooseBotRoot(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PortionViewModel model)
            return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = MeowsText.Current["portion.dialog.botfolder"],
            AllowMultiple = false,
        });

        var picked = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(picked))
            model.SetBotRoot(picked);
    }

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
