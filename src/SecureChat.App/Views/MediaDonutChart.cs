using Microsoft.Maui.Graphics;

namespace SecureChat.App.Views;

/// <summary>Сегмент круговой диаграммы.</summary>
public sealed record MediaDonutSegment(Color Color, double Value);

/// <summary>Круговая диаграмма занятой памяти по чатам, как в Telegram.</summary>
public sealed class MediaDonutChart : GraphicsView
{
    public static readonly BindableProperty SegmentsProperty =
        BindableProperty.Create(nameof(Segments), typeof(IReadOnlyList<MediaDonutSegment>),
            typeof(MediaDonutChart), Array.Empty<MediaDonutSegment>(),
            propertyChanged: (b, _, _) => ((MediaDonutChart)b).Refresh());

    public IReadOnlyList<MediaDonutSegment> Segments
    {
        get => (IReadOnlyList<MediaDonutSegment>)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    private sealed class DonutDrawable : IDrawable
    {
        public required IReadOnlyList<MediaDonutSegment> Segments { get; init; }

        public void Draw(ICanvas canvas, RectF rect)
        {
            var segments = Segments.Where(s => s.Value > 0).ToList();
            if (segments.Count == 0) return;

            var total = segments.Sum(s => s.Value);
            var side = Math.Min(rect.Width, rect.Height);
            var radius = Math.Max(0, side / 2 - 12);
            if (radius <= 0) return;

            var centerX = rect.Left + rect.Width / 2;
            var centerY = rect.Top + rect.Height / 2;
            var stroke = Math.Max(10f, (float)(radius * 0.34));

            var start = -90f;
            foreach (var segment in segments)
            {
                var sweep = (float)(segment.Value / total * 360);
                if (sweep <= 0) continue;
                canvas.StrokeColor = segment.Color;
                canvas.StrokeSize = stroke;
                canvas.DrawArc(
                    centerX - radius, centerY - radius,
                    radius * 2, radius * 2,
                    start, sweep, false, false);
                start += sweep;
            }
        }
    }

    private void Refresh() => Drawable = new DonutDrawable { Segments = Segments };
}
