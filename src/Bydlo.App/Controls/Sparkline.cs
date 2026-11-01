using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Bydlo.App.Controls;

/// <summary>Lightweight live area chart for the last N traffic samples.</summary>
public sealed class Sparkline : Control
{
    public static readonly StyledProperty<IEnumerable<double>?> ValuesProperty =
        AvaloniaProperty.Register<Sparkline, IEnumerable<double>?>(nameof(Values));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<int> CapacityProperty =
        AvaloniaProperty.Register<Sparkline, int>(nameof(Capacity), 60);

    static Sparkline()
    {
        AffectsRender<Sparkline>(ValuesProperty, StrokeProperty);
    }

    public IEnumerable<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public int Capacity
    {
        get => GetValue(CapacityProperty);
        set => SetValue(CapacityProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValuesProperty)
        {
            if (change.OldValue is INotifyCollectionChanged oldC)
            {
                oldC.CollectionChanged -= OnCollectionChanged;
            }

            if (change.NewValue is INotifyCollectionChanged newC)
            {
                newC.CollectionChanged += OnCollectionChanged;
            }
        }
    }

    public override void Render(DrawingContext context)
    {
        var values = Values?.ToArray() ?? [];
        var w = Bounds.Width;
        var h = Bounds.Height;
        var stroke = Stroke ?? Brushes.MediumPurple;
        if (values.Length < 2 || w <= 0 || h <= 0)
        {
            context.DrawLine(new Pen(stroke, 1, DashStyle.Dash), new Point(0, h - 1), new Point(w, h - 1));
            return;
        }

        var max = Math.Max(values.Max(), 1);
        var step = w / Math.Max(Capacity - 1, 1);
        var offset = w - (values.Length - 1) * step;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(offset, h), true);
            for (var i = 0; i < values.Length; i++)
            {
                g.LineTo(new Point(offset + i * step, h - 2 - (values[i] / max) * (h - 6)));
            }

            g.LineTo(new Point(offset + (values.Length - 1) * step, h));
            g.EndFigure(true);
        }

        var color = (stroke as ISolidColorBrush)?.Color ?? Colors.MediumPurple;
        var fill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(90, color.R, color.G, color.B), 0),
                new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1),
            },
        };
        context.DrawGeometry(fill, null, geometry);

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(new Point(offset, h - 2 - (values[0] / max) * (h - 6)), false);
            for (var i = 1; i < values.Length; i++)
            {
                g.LineTo(new Point(offset + i * step, h - 2 - (values[i] / max) * (h - 6)));
            }

            g.EndFigure(false);
        }

        context.DrawGeometry(null, new Pen(stroke, 2, lineJoin: PenLineJoin.Round), line);
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();
}
