using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Mews.Plugins.Chonk.ViewModels;

namespace Mews.Plugins.Chonk.Views;

public partial class ChonkView : UserControl, IDisposable
{
    public ChonkView()
    {
        InitializeComponent();
        this.FindControl<Button>("PickFolderButton")!.Click += OnPickFolder;

        var list = this.FindControl<ListBox>("EntryList")!;
        list.DoubleTapped += OnRowActivated;

        // Hooked on the visual tree, where TopLevel actually exists. Escape has to work
        // wherever focus happens to be, which is the one thing a confirmation must not depend on.
        AttachedToVisualTree += (_, _) => HookKeys();
        DetachedFromVisualTree += (_, _) => UnhookKeys();
    }

    private TopLevel? _keySource;

    private void HookKeys()
    {
        if (_keySource is not null)
            return;

        _keySource = TopLevel.GetTopLevel(this);
        _keySource?.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private void UnhookKeys()
    {
        _keySource?.RemoveHandler(KeyDownEvent, OnKeyDown);
        _keySource = null;
    }

    /// <summary>Escape backs out of the confirmation, the way every other one does.</summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsEffectivelyVisible || Model is not { IsAsking: true } model)
            return;

        if (e.Key != Key.Escape)
            return;

        model.CancelDeleteCommand.Execute(null);
        e.Handled = true;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private ChonkViewModel? Model => DataContext as ChonkViewModel;

    /// <summary>Double click opens a folder, which is what every file manager has trained you to expect.</summary>
    private void OnRowActivated(object? sender, TappedEventArgs e)
    {
        if (Model is { Selected: { CanDrillInto: true } row } model)
            model.OpenCommand.Execute(row);
    }

    private async void OnPickFolder(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model)
            return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Measure which folder",
            AllowMultiple = false,
        });

        var picked = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(picked))
            model.StartScan(picked);
    }

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
