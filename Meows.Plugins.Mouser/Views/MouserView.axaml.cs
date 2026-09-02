using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Meows.Plugins.Mouser.ViewModels;

namespace Meows.Plugins.Mouser.Views;

public partial class MouserView : UserControl, IDisposable
{
    private TopLevel? _keySource;

    public MouserView()
    {
        InitializeComponent();
        this.FindControl<Button>("PickFolderButton")!.Click += OnPickFolder;
        this.FindControl<ListBox>("FindingList")!.SelectionChanged += OnSelectionChanged;

        // The visual tree, not the logical one: TopLevel is still null when the logical tree
        // attaches, so hooking there gets you a handler on nothing.
        AttachedToVisualTree += (_, _) => HookKeys();
        DetachedFromVisualTree += (_, _) => UnhookKeys();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private MouserViewModel? Model => DataContext as MouserViewModel;

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
            model.SetSelection(list.SelectedItems?.OfType<FindingViewModel>() ?? []);
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
            Title = "Which folder should be looked through",
            AllowMultiple = false,
        });

        var picked = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(picked))
            model.SetRoot(picked);
    }

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
