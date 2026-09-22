using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Meows.ViewModels;

namespace Meows.Views;

/// <summary>
/// Dragging on the tab strip: a tab to anywhere on it, a group's chip to reorder the groups.
///
/// Done with the pointer directly rather than through <see cref="DragDrop"/>. The strip is one
/// control, nothing leaves it and nothing arrives from outside it, so the whole of what the
/// platform's drag machinery adds here is a data object nobody reads, a second set of events to
/// keep in step, and a drag image Windows draws in the wrong place over a wrapping panel. Pointer
/// capture gives the same thing and keeps the hit testing ours.
///
/// The one rule worth stating: nothing moves until the pointer has travelled far enough to mean
/// it. A strip where a slightly shaky click reorders the tabs would be worse than one that does
/// not move at all.
/// </summary>
public sealed class TabStripDrag
{
    /// <summary>How far the pointer has to go before a press becomes a drag, in pixels.</summary>
    private const double Threshold = 6;

    private readonly ItemsControl _strip;
    private readonly Func<TabStripViewModel?> _model;

    private object? _dragging;
    private Point _from;
    private bool _moved;

    public TabStripDrag(ItemsControl strip, Func<TabStripViewModel?> model)
    {
        _strip = strip;
        _model = model;

        // Tunnelling, so a press is seen before the button under the pointer handles it and
        // takes the event away.
        _strip.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        _strip.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
        _strip.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
        _strip.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => Stop());
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_strip).Properties.IsLeftButtonPressed)
            return;

        _dragging = ItemUnder(e.GetPosition(_strip));
        _from = e.GetPosition(_strip);
        _moved = false;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_dragging is null)
            return;

        var at = e.GetPosition(_strip);
        if (!_moved && Distance(_from, at) < Threshold)
            return;

        _moved = true;

        var model = _model();
        if (model is null)
            return;

        model.ClearHints();

        // Where it would land if the pointer were let go now.
        var (target, before) = TargetUnder(at);
        if (target is null || ReferenceEquals(target, _dragging))
            return;

        // A chip only ever lands beside another chip, and a tab can land beside either.
        if (_dragging is GroupChipViewModel && target is not GroupChipViewModel)
            return;

        var hint = before ? DropHint.Before : DropHint.After;
        switch (target)
        {
            case TabViewModel tab:
                tab.Hint = hint;
                break;
            case GroupChipViewModel chip:
                chip.Hint = hint;
                break;
        }
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        var dragging = _dragging;
        var moved = _moved;
        var model = _model();
        Stop();

        // A press that never travelled is a click, and the button under it gets on with being
        // clicked: this handler tunnels, so not handling the event leaves it alone.
        if (!moved || dragging is null || model is null)
            return;

        var (target, before) = TargetUnder(e.GetPosition(_strip));
        if (target is null || ReferenceEquals(target, dragging))
            return;

        switch (dragging)
        {
            case TabViewModel tab when target is TabViewModel onto:
                model.DropTab(tab.Key, onto.Key, after: !before);
                break;

            case TabViewModel tab when target is GroupChipViewModel chip:
                model.DropTabOnGroup(tab.Key, chip.Key, atFront: before);
                break;

            case GroupChipViewModel chip when target is GroupChipViewModel onto:
                model.DropGroup(chip.Key, onto.Key, after: !before);
                break;
        }

        // The release finished a drag rather than a click, so the button under it must not also
        // be pressed: dropping a tab should not select whatever it was dropped on.
        e.Handled = true;
    }

    private void Stop()
    {
        _dragging = null;
        _moved = false;
        _model()?.ClearHints();
    }

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>The strip item under a point, or null over the strip's own background.</summary>
    private object? ItemUnder(Point at) => Element(at)?.DataContext;

    /// <summary>
    /// What a drop at this point would land against, and whether it would go before it. Which
    /// side is decided on the horizontal middle of the item, because the strip runs horizontally
    /// even when it wraps onto a second row.
    /// </summary>
    private (object? Target, bool Before) TargetUnder(Point at)
    {
        var element = Element(at);
        if (element?.DataContext is not { } item)
            return (null, false);

        // The element's own Bounds are in its parent's space, so the left edge is asked for in
        // the strip's space instead. Nesting inside the item template then cannot affect it.
        var origin = element.TranslatePoint(new Point(0, 0), _strip) ?? new Point(element.Bounds.X, element.Bounds.Y);
        return (item, at.X < origin.X + element.Bounds.Width / 2);
    }

    /// <summary>
    /// The strip item under a point: whatever was hit, walked up until something holding a tab
    /// or a chip is found. The pointer is usually over a TextBlock inside a Button inside the
    /// item, and how deep that goes is the item template's business rather than this one's.
    /// </summary>
    private Control? Element(Point at)
    {
        var hit = _strip.InputHitTest(at) as Visual;
        while (hit is not null)
        {
            if (hit is Control { DataContext: TabViewModel or GroupChipViewModel } control)
                return control;
            hit = hit.GetVisualParent();
        }

        return null;
    }
}
