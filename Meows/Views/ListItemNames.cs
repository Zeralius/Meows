using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;

namespace Meows.Views;

/// <summary>
/// What a screen reader says for a row in a list: the words the row shows. A row whose item is a
/// view model was announced as nothing at all, or as the view model's type name, in every one of
/// the hundred-odd lists the shell and the plugins have; naming each by hand would be a hundred
/// edits and a rule every new list would have to remember.
///
/// So when a row takes focus, which is when it is read out, and it has no name of its own, it
/// is given the text of the first few lines it shows. A row that says what it is with an explicit
/// <c>AutomationProperties.Name</c> keeps that.
/// </summary>
public static class ListItemNames
{
    /// <summary>How many of a row's lines are read: the name and a detail or two, not the whole card.</summary>
    private const int Lines = 3;

    private static readonly AttachedProperty<bool> Given =
        AvaloniaProperty.RegisterAttached<Control, bool>("NameGiven", typeof(ListItemNames));

    private static bool _registered;

    /// <summary>Once per process; the handlers are on the classes, not on any one window.</summary>
    public static void Register()
    {
        if (_registered)
            return;
        _registered = true;
        InputElement.GotFocusEvent.AddClassHandler<ListBoxItem>((item, _) => Name(item));
        InputElement.GotFocusEvent.AddClassHandler<TreeViewItem>((item, _) => Name(item));
        InputElement.GotFocusEvent.AddClassHandler<ComboBoxItem>((item, _) => Name(item));
    }

    /// <summary>Names the row from what it shows, unless it was named some other way.</summary>
    public static void Name(Control item)
    {
        var own = AutomationProperties.GetName(item);
        if (!string.IsNullOrEmpty(own) && !item.GetValue(Given))
            return;

        var words = WordsOf(item);
        if (words.Length == 0)
            return;

        AutomationProperties.SetName(item, words);
        item.SetValue(Given, true);
    }

    /// <summary>The visible text of a row, its first few lines, joined the way a reader pauses.</summary>
    public static string WordsOf(Control item)
    {
        var lines = item.GetLogicalDescendants()
            .TakeWhile(d => item is not TreeViewItem || d is not TreeViewItem)
            .OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text))
            .Select(t => t.Text!.Trim())
            .Distinct()
            .Take(Lines);
        return string.Join(", ", lines);
    }
}
