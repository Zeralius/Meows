using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Meows.Plugins.Familiar.ViewModels;

namespace Meows.Plugins.Familiar.Views;

public partial class FamiliarView : UserControl, IDisposable
{
    public FamiliarView()
    {
        InitializeComponent();

        // Enter in the name box renames, the way a name box in a file manager does.
        var name = this.FindControl<TextBox>("NameBox")!;
        name.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Model is { } model && model.RenameCommand.CanExecute(null))
            {
                model.RenameCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private FamiliarViewModel? Model => DataContext as FamiliarViewModel;

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
