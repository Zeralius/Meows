using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Meows.Plugins.Tin.ViewModels;

namespace Meows.Plugins.Tin.Views;

/// <summary>
/// The wedges, drawn.
///
/// A ring rather than a full pie: the middle is where the total goes, and a wedge is easier to
/// compare against its neighbour by its edge than by its point.
///
/// Avalonia ships no chart, and a chart library for one ring would be a dependency to stage into
/// every release. This is forty lines of arcs.
/// </summary>
public sealed class Donut : Control
{
    public static readonly StyledProperty<IEnumerable<SliceViewModel>?> SlicesProperty =
        AvaloniaProperty.Register<Donut, IEnumerable<SliceViewModel>?>(nameof(Slices));

    static Donut()
    {
        AffectsRender<Donut>(SlicesProperty);
        AffectsMeasure<Donut>(SlicesProperty);
    }

    public IEnumerable<SliceViewModel>? Slices
    {
        get => GetValue(SlicesProperty);
        set => SetValue(SlicesProperty, value);
    }

    /// <summary>
    /// A control with no content of its own is measured at nothing, and nothing is what it then
    /// gets drawn in. Inside a scroll viewer the width offered can also be infinite, which needs
    /// an answer that is a number.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? width : availableSize.Height;

        return new Size(width, height);
    }

    /// <summary>
    /// The list handed over is the same object every time; only its contents change. Without this
    /// the ring is drawn once, on whatever was in it when the tab opened, and never again.
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != SlicesProperty)
            return;

        if (change.OldValue is INotifyCollectionChanged old)
            old.CollectionChanged -= OnSlicesChanged;

        if (change.NewValue is INotifyCollectionChanged fresh)
            fresh.CollectionChanged += OnSlicesChanged;

        InvalidateVisual();
    }

    private void OnSlicesChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        var slices = Slices?.Where(s => s.Share > 0).ToList();
        if (slices is null || slices.Count == 0)
            return;

        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 8)
            return;

        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var outer = size / 2 - 1;
        var inner = outer * 0.58;

        // Twelve o'clock, going clockwise, because that is where a reader starts looking.
        var angle = -Math.PI / 2;

        foreach (var slice in slices)
        {
            // A single wedge covering everything would start and end in the same place, which
            // draws nothing at all. A hair short of the full turn draws a ring.
            var sweep = Math.Min(slice.Share, 0.9995) * Math.PI * 2;

            context.DrawGeometry(slice.Fill, null, Wedge(centre, inner, outer, angle, angle + sweep));
            angle += sweep;
        }
    }

    private static StreamGeometry Wedge(Point centre, double inner, double outer, double from, double to)
    {
        var geometry = new StreamGeometry();
        using var draw = geometry.Open();

        var big = to - from > Math.PI;

        draw.BeginFigure(On(centre, outer, from), true);
        draw.ArcTo(On(centre, outer, to), new Size(outer, outer), 0, big, SweepDirection.Clockwise);
        draw.LineTo(On(centre, inner, to));
        draw.ArcTo(On(centre, inner, from), new Size(inner, inner), 0, big, SweepDirection.CounterClockwise);
        draw.EndFigure(true);

        return geometry;
    }

    private static Point On(Point centre, double radius, double angle) =>
        new(centre.X + radius * Math.Cos(angle), centre.Y + radius * Math.Sin(angle));
}
