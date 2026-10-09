using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PokeStudio.App.Controls;

/// <summary>Six-axis stat chart: current values filled, original values dashed. Values are also shown as numbers, so colour isn't needed to read it.</summary>
public sealed class RadarChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<RadarChart, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<IReadOnlyList<double>?> OriginalProperty =
        AvaloniaProperty.Register<RadarChart, IReadOnlyList<double>?>(nameof(Original));

    private static readonly string[] Labels = ["HP", "Attack", "Defense", "Speed", "Sp. Def", "Sp. Atk"];

    static RadarChart() => AffectsRender<RadarChart>(ValuesProperty, OriginalProperty);

    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IReadOnlyList<double>? Original
    {
        get => GetValue(OriginalProperty);
        set => SetValue(OriginalProperty, value);
    }

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : fallback;

    public override void Render(DrawingContext context)
    {
        var values = Values;
        if (values is not { Count: 6 })
        {
            return;
        }

        var size = Math.Min(Bounds.Width, Bounds.Height);
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = size / 2 - 34;
        var max = Math.Max(160, Math.Max(values.Max(), Original?.Max() ?? 0));
        var grid = new Pen(Brush("PL.BorderBrush", Brushes.Gray), 1);
        var text = Brush("PL.Text2Brush", Brushes.Gray);
        var primary = Brush("PL.PrimaryBrush", Brushes.MediumPurple);

        Point At(int i, double v)
        {
            var angle = (-90 + 60 * i) * Math.PI / 180;
            var r = radius * Math.Clamp(v / max, 0, 1);
            return new Point(center.X + r * Math.Cos(angle), center.Y + r * Math.Sin(angle));
        }

        StreamGeometry Polygon(IReadOnlyList<double> v)
        {
            var g = new StreamGeometry();
            using var ctx = g.Open();
            ctx.BeginFigure(At(0, v[0]), true);
            for (var i = 1; i < 6; i++)
            {
                ctx.LineTo(At(i, v[i]));
            }

            ctx.EndFigure(true);
            return g;
        }

        foreach (var f in new[] { 0.33, 0.66, 1.0 })
        {
            context.DrawGeometry(null, grid, Polygon([max * f, max * f, max * f, max * f, max * f, max * f]));
        }

        for (var i = 0; i < 6; i++)
        {
            context.DrawLine(grid, center, At(i, max));
        }

        if (Original is { Count: 6 } original && !original.SequenceEqual(values))
        {
            context.DrawGeometry(null, new Pen(text, 1.5, new DashStyle([4, 3], 0)), Polygon(original));
        }

        var fill = primary is ISolidColorBrush s ? new SolidColorBrush(s.Color, 0.28) : primary;
        context.DrawGeometry(fill, new Pen(primary, 2.5, lineJoin: PenLineJoin.Round), Polygon(values));

        var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);
        for (var i = 0; i < 6; i++)
        {
            var label = new FormattedText($"{Labels[i]} {values[i]:0}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 12, text);
            var p = At(i, max * 1.18);
            context.DrawText(label, new Point(p.X - label.Width / 2, p.Y - label.Height / 2));
        }
    }
}
