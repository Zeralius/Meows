using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Meows.Plugins.Saucer.ViewModels;

namespace Meows.Plugins.Saucer.Views;

public partial class SaucerView : UserControl, IDisposable
{
    public SaucerView()
    {
        InitializeComponent();
        this.FindControl<Button>("PickIntakeButton")!.Click += OnPickIntake;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private SaucerViewModel? Model => DataContext as SaucerViewModel;

    private async void OnPickIntake(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model)
            return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Where saved clippings should land",
            AllowMultiple = false,
        });

        var picked = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(picked))
            model.SetIntakeFolder(picked);
    }

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
