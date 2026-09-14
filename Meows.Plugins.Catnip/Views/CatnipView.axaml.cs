using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Catnip.ViewModels;

namespace Meows.Plugins.Catnip.Views;

public partial class CatnipView : UserControl, IDisposable
{
    public CatnipView()
    {
        InitializeComponent();
        this.FindControl<Button>("AddRootButton")!.Click += OnAddRoot;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnAddRoot(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CatnipViewModel model || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = MeowsText.Current["catnip.dialog.root"], AllowMultiple = true });
        foreach (var folder in folders.Select(f => f.TryGetLocalPath()).Where(p => p is not null))
            model.AddRoot(folder!);
    }

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
