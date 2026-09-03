using Meows.Plugins.Abstractions;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Meows.Plugins.Molt.ViewModels;

namespace Meows.Plugins.Molt.Views;

public partial class MoltView : UserControl, IDisposable
{
    private TopLevel? _keySource;

    public MoltView()
    {
        InitializeComponent();
        this.FindControl<Button>("PickBuildRootButton")!.Click += OnPickBuildRoot;

        AttachedToVisualTree += (_, _) => HookKeys();
        DetachedFromVisualTree += (_, _) => UnhookKeys();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private MoltViewModel? Model => DataContext as MoltViewModel;

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

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsEffectivelyVisible || Model is not { IsAsking: true } model || e.Key != Key.Escape)
            return;

        model.CancelShedCommand.Execute(null);
        e.Handled = true;
    }

    private async void OnPickBuildRoot(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model)
            return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = MeowsText.Current["molt.dialog.folder"],
            AllowMultiple = false,
        });

        var picked = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(picked))
            model.SetBuildRoot(picked);
    }

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
