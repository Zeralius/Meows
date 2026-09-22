using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Meows.Plugins.Rehome.ViewModels;

namespace Meows.Plugins.Rehome.Views;

public partial class RehomeView : UserControl, IDisposable
{
    public RehomeView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is RehomeViewModel model)
                model.CopyText = text => _ = CopyAsync(text);
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async Task CopyAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null || text.Length == 0)
            return;
        try
        {
            await clipboard.SetTextAsync(text);
        }
        catch (Exception)
        {
            // Another program has the clipboard open. Pressing the button again is the fix.
        }
    }

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
