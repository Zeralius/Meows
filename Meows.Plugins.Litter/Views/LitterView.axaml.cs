using Meows.Plugins.Abstractions;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Meows.Plugins.Litter.ViewModels;

namespace Meows.Plugins.Litter.Views;

public partial class LitterView : UserControl, IDisposable
{
    private TopLevel? _keySource;

    public LitterView()
    {
        InitializeComponent();
        this.FindControl<Button>("PickFolderButton")!.Click += OnPickFolder;
        this.FindControl<ListBox>("ItemList")!.SelectionChanged += OnSelectionChanged;

        AttachedToVisualTree += (_, _) => HookKeys();
        DetachedFromVisualTree += (_, _) => UnhookKeys();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private LitterViewModel? Model => DataContext as LitterViewModel;

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

    /// <summary>Escape backs out of the confirmation, wherever focus happens to be.</summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsEffectivelyVisible || Model is not { IsAsking: true } model || e.Key != Key.Escape)
            return;

        model.CancelDeleteCommand.Execute(null);
        e.Handled = true;
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox list && Model is { } model)
            model.SetSelection(list.SelectedItems?.OfType<ItemViewModel>() ?? []);
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
            Title = MeowsText.Current["litter.dialog.folder"],
            AllowMultiple = false,
        });

        var picked = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(picked))
            model.SetFolder(picked);
    }

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
