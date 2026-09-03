using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Birdwatch.ViewModels;

namespace Meows.Plugins.Birdwatch.Views;

public partial class BirdwatchView : UserControl
{
    public BirdwatchView()
    {
        InitializeComponent();
        this.FindControl<Button>("PickIntakeButton")!.Click += OnPickIntake;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnPickIntake(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not BirdwatchViewModel model)
            return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return;

        var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = MeowsText.Current["birdwatch.dialog.intake"],
            AllowMultiple = false,
        });

        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path)
            model.SetIntakeFolder(path);
    }
}
