using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Meows.Plugins.Abstractions;
using Meows.ViewModels;

namespace Meows.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        this.FindControl<Button>("ExportButton")!.Click += OnExport;
        this.FindControl<Button>("ImportButton")!.Click += OnImport;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private SettingsViewModel? Model => DataContext as SettingsViewModel;

    private static FilePickerFileType Zip => new("Meows settings bundle") { Patterns = ["*.zip"] };

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = MeowsText.Current["settings.bundle.export"],
            SuggestedFileName = model.SuggestedBundleName,
            DefaultExtension = "zip",
            FileTypeChoices = [Zip],
        });

        if (file?.TryGetLocalPath() is { } path)
            model.Export(path);
    }

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = MeowsText.Current["settings.bundle.import"],
            AllowMultiple = false,
            FileTypeFilter = [Zip],
        });

        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
            model.Import(path);
    }
}
