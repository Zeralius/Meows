using Meows.Plugins.Abstractions;

namespace Meows.ViewModels;

/// <summary>
/// One line of text asked for over the window: a group's new name, a new group's name. It was a
/// text box inside a menu item, which a menu's own keys fight over, so typing into it, leaving it
/// or reaching it with a screen reader was awkward. A small card with a box, OK and Cancel is not.
/// </summary>
public sealed class PromptViewModel : ObservableObject
{
    private bool _isOpen;
    private string _title = "";
    private string _text = "";
    private Action<string>? _done;

    public PromptViewModel()
    {
        OkCommand = new RelayCommand(Ok, () => !string.IsNullOrWhiteSpace(Text));
        CancelCommand = new RelayCommand(Cancel);
    }

    public bool IsOpen
    {
        get => _isOpen;
        private set => SetField(ref _isOpen, value);
    }

    public string Title
    {
        get => _title;
        private set => SetField(ref _title, value);
    }

    public string Text
    {
        get => _text;
        set
        {
            if (SetField(ref _text, value))
                OkCommand.RaiseCanExecuteChanged();
        }
    }

    public RelayCommand OkCommand { get; }

    public RelayCommand CancelCommand { get; }

    /// <summary>Opens the card holding <paramref name="initial"/>; <paramref name="done"/> gets the text on OK.</summary>
    public void Ask(string title, string initial, Action<string> done)
    {
        Title = title;
        Text = initial;
        _done = done;
        IsOpen = true;
    }

    private void Ok()
    {
        var done = _done;
        var text = Text.Trim();
        Cancel();
        if (text.Length > 0)
            done?.Invoke(text);
    }

    private void Cancel()
    {
        _done = null;
        IsOpen = false;
    }
}
