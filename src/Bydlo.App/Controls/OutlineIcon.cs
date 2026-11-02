using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Bydlo.App.Controls;

/// <summary>Consistent, optically aligned icons drawn on a 24-unit grid.</summary>
public sealed class OutlineIcon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty = AvaloniaProperty.Register<OutlineIcon, Geometry?>(nameof(Data));
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<OutlineIcon>();

    static OutlineIcon() => AffectsRender<OutlineIcon>(DataProperty, ForegroundProperty);

    public Geometry? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Data is null || Foreground is null) return;
        using (context.PushTransform(Matrix.CreateScale(Bounds.Width / 24, Bounds.Height / 24)))
        {
            context.DrawGeometry(null, new Pen(Foreground, 1.65, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), Data);
        }
    }
}
