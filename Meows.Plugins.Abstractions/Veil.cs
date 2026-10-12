using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;

namespace Meows.Plugins.Abstractions;

/// <summary>
/// For a confirmation drawn over a view: a veil with a card on it, shown by binding its
/// <c>IsVisible</c>. Set <c>m:Veil.HoldsFocus="True"</c> on the veil and the keyboard comes with
/// it: focus moves to the card's safe button when it appears, Tab stays inside the card until it
/// is answered, and focus goes back where it was when it closes.
///
/// Without it, focus stays on the control behind the veil, so Tab walks through what the veil is
/// covering and a screen reader never hears the question. The safe button is the one with the
/// <c>safe</c> class, or the first button in the card if none has it, which is Cancel wherever the
/// card puts Cancel on the left.
///
/// Since contract 1.8.0.
/// </summary>
public static class Veil
{
    public static readonly AttachedProperty<bool> HoldsFocusProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("HoldsFocus", typeof(Veil));

    private static readonly ConditionalWeakTable<Control, IInputElement> Before = new();

    static Veil()
    {
        HoldsFocusProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue is not true)
                return;
            KeyboardNavigation.SetTabNavigation(control, KeyboardNavigationMode.Cycle);
            control.PropertyChanged += OnPropertyChanged;
            if (control.IsVisible)
                Shown(control);
        });
    }

    public static bool GetHoldsFocus(Control control) => control.GetValue(HoldsFocusProperty);

    public static void SetHoldsFocus(Control control, bool value) => control.SetValue(HoldsFocusProperty, value);

    private static void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is not Control control || e.Property != Visual.IsVisibleProperty)
            return;
        if (control.IsVisible)
            Shown(control);
        else
            Hidden(control);
    }

    private static void Shown(Control control)
    {
        if (TopLevel.GetTopLevel(control)?.FocusManager?.GetFocusedElement() is { } focused)
            Before.AddOrUpdate(control, focused);

        // After layout, so the buttons the binding just revealed can take focus.
        Dispatcher.UIThread.Post(() =>
        {
            var buttons = control.GetLogicalDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToList();
            var safe = buttons.FirstOrDefault(b => b.Classes.Contains("safe")) ?? buttons.FirstOrDefault();
            safe?.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Background);
    }

    private static void Hidden(Control control)
    {
        if (!Before.TryGetValue(control, out var back))
            return;
        Before.Remove(control);
        if (back is Visual visual && TopLevel.GetTopLevel(visual) is not null && back.IsEffectivelyVisible)
            Dispatcher.UIThread.Post(() => back.Focus(), DispatcherPriority.Background);
    }
}
