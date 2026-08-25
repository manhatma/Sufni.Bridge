using System;
using System.Globalization;
using System.IO;
using Avalonia.Media.Imaging;
using SkiaSharp;
using Sufni.Bridge.Models;

namespace Sufni.Bridge.Plots;

public static class TrackMapRenderer
{
    private static readonly SKColor DataBackground = new(0x20, 0x26, 0x2B);
    private static readonly SKColor TrackLine = new(0x80, 0xA8, 0xB8, 0xC0);
    private static readonly SKColor HighlightLine = new(0xFE, 0xFE, 0xFE);
    private static readonly SKColor StartMarker = new(0x66, 0xC2, 0xA5);
    private static readonly SKColor EndMarker = new(0xE3, 0x4A, 0x33);
    private static readonly SKColor AttributionColor = new(0xD0, 0xD0, 0xD0);
    private static readonly SKColor ScaleBackdrop = new(0x15, 0x19, 0x1C, 0xC8);
    private const string Attribution = "Esri, Maxar, Earthstar Geographics";
    private const int TurboStops = 16;

    public static Bitmap Render(
        SessionTrack track,
        SKBitmap? tiles,
        int width,
        int height,
        ZoomWindow? highlight,
        TrackOverlay? overlay = null)
    {
        if (width < 1) width = 1;
        if (height < 1) height = 1;

        var extent = track.Bounds.Pad(0.10);

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(DataBackground);

        if (tiles is not null)
            canvas.DrawBitmap(tiles, SKRect.Create(0, 0, width, height));

        if (!track.IsEmpty && extent.Width > 0 && extent.Height > 0)
        {
            var strokeWidth = Math.Max(2f, width / 180f);
            if (overlay is not null)
                DrawOverlay(canvas, track, overlay, extent, width, height, strokeWidth);
            else
            {
                using var muted = Stroke(TrackLine, strokeWidth);
                foreach (var segment in track.Segments)
                    DrawSegment(canvas, segment, extent, width, height, muted, null);
            }

            if (highlight is not null && highlight.EndSeconds > highlight.StartSeconds)
            {
                using var bright = Stroke(HighlightLine, Math.Max(2.8f, width / 140f));
                foreach (var segment in track.Segments)
                    DrawSegment(canvas, segment, extent, width, height, bright, highlight);
            }

            DrawEndpoints(canvas, track, extent, width, height);
        }

        if (overlay is not null)
            DrawColorBar(canvas, overlay, width);

        using var textPaint = new SKPaint
        {
            IsAntialias = true,
            Color = AttributionColor,
            TextSize = Math.Max(8f, width / 52f)
        };
        var textWidth = textPaint.MeasureText(Attribution);
        canvas.DrawText(Attribution, width - 6 - textWidth, height - 6, textPaint);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream();
        data.SaveTo(stream);
        stream.Position = 0;
        return new Bitmap(stream);
    }

    private static SKPaint Stroke(SKColor color, float width) => new()
    {
        IsAntialias = true,
        IsStroke = true,
        Color = color,
        StrokeWidth = width,
        StrokeCap = SKStrokeCap.Round,
        StrokeJoin = SKStrokeJoin.Round,
        Style = SKPaintStyle.Stroke
    };

    private static void DrawOverlay(
        SKCanvas canvas,
        SessionTrack track,
        TrackOverlay overlay,
        MapBounds extent,
        int width,
        int height,
        float strokeWidth)
    {
        using var paint = Stroke(TrackLine, strokeWidth);
        var turbo = new ScottPlot.Colormaps.Turbo();
        var segmentCount = Math.Min(track.Segments.Count, overlay.SegmentPairValues.Count);
        for (var s = 0; s < segmentCount; s++)
        {
            var segment = track.Segments[s];
            var values = overlay.SegmentPairValues[s];
            var edges = Math.Min(values.Length, Math.Max(0, segment.X.Length - 1));
            for (var i = 0; i < edges; i++)
            {
                paint.Color = ColorFor(values[i], overlay.Min, overlay.Max, turbo);
                ToPixel(segment.X[i], segment.Y[i], extent, width, height, out var x0, out var y0);
                ToPixel(segment.X[i + 1], segment.Y[i + 1], extent, width, height, out var x1, out var y1);
                canvas.DrawLine(x0, y0, x1, y1, paint);
            }
        }
    }

    private static SKColor ColorFor(double value, double min, double max, ScottPlot.Colormaps.Turbo turbo)
    {
        if (!double.IsFinite(value))
            return TrackLine;
        double frac;
        if (!double.IsFinite(min) || !double.IsFinite(max) || max <= min)
            frac = 0.5;
        else
            frac = Math.Clamp((value - min) / (max - min), 0.0, 1.0);
        var color = turbo.GetColor(frac);
        return new SKColor(color.R, color.G, color.B);
    }

    private static void DrawColorBar(SKCanvas canvas, TrackOverlay overlay, int width)
    {
        var textSize = Math.Max(9f, width / 48f);
        using var textPaint = new SKPaint
        {
            IsAntialias = true,
            Color = AttributionColor,
            TextSize = textSize
        };

        var minLabel = FormatScale(overlay.Min);
        var maxLabel = $"{FormatScale(overlay.Max)} {overlay.Unit}";
        var minWidth = textPaint.MeasureText(minLabel);
        var maxWidth = textPaint.MeasureText(maxLabel);

        var margin = Math.Max(8f, width / 50f);
        var gap = Math.Max(6f, width / 80f);
        var barHeight = Math.Max(8f, width / 70f);
        var barLeft = margin + minWidth + gap;
        var barRight = width - margin - maxWidth - gap;
        if (barRight - barLeft < 24f)
            return;

        var barTop = margin;
        var backdrop = new SKRect(
            margin - 4f,
            barTop - 4f,
            width - margin + 4f,
            barTop + barHeight + 4f);
        using var backdropPaint = new SKPaint
        {
            IsAntialias = true,
            Color = ScaleBackdrop,
            Style = SKPaintStyle.Fill
        };
        canvas.DrawRoundRect(backdrop, 4f, 4f, backdropPaint);

        var turbo = new ScottPlot.Colormaps.Turbo();
        var colors = new SKColor[TurboStops];
        var positions = new float[TurboStops];
        for (var i = 0; i < TurboStops; i++)
        {
            var frac = i / (float)(TurboStops - 1);
            var color = turbo.GetColor(frac);
            colors[i] = new SKColor(color.R, color.G, color.B);
            positions[i] = frac;
        }

        var barRect = new SKRect(barLeft, barTop, barRight, barTop + barHeight);
        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(barRect.Left, barRect.MidY),
            new SKPoint(barRect.Right, barRect.MidY),
            colors,
            positions,
            SKShaderTileMode.Clamp);
        using var barPaint = new SKPaint
        {
            IsAntialias = true,
            Shader = shader,
            Style = SKPaintStyle.Fill
        };
        canvas.DrawRoundRect(barRect, 2f, 2f, barPaint);

        var textY = barTop + barHeight - (barHeight - textSize) * 0.35f;
        canvas.DrawText(minLabel, margin, textY, textPaint);
        canvas.DrawText(maxLabel, barRight + gap, textY, textPaint);
    }

    private static string FormatScale(double value)
    {
        if (!double.IsFinite(value))
            return "–";
        var abs = Math.Abs(value);
        var format = abs >= 100 ? "0" : "0.0";
        return value.ToString(format, CultureInfo.InvariantCulture);
    }

    private static void DrawSegment(
        SKCanvas canvas,
        TrackSegment segment,
        MapBounds extent,
        int width,
        int height,
        SKPaint paint,
        ZoomWindow? window)
    {
        using var path = new SKPath();
        var started = false;
        for (var i = 0; i < segment.X.Length; i++)
        {
            if (window is not null &&
                (segment.TimeSeconds[i] < window.StartSeconds || segment.TimeSeconds[i] > window.EndSeconds))
            {
                started = false;
                continue;
            }

            ToPixel(segment.X[i], segment.Y[i], extent, width, height, out var px, out var py);
            if (!started)
            {
                path.MoveTo(px, py);
                started = true;
            }
            else
            {
                path.LineTo(px, py);
            }
        }

        if (!path.IsEmpty)
            canvas.DrawPath(path, paint);
    }

    private static void DrawEndpoints(SKCanvas canvas, SessionTrack track, MapBounds extent, int width, int height)
    {
        var first = track.Segments[0];
        var last = track.Segments[^1];
        if (first.X.Length == 0 || last.X.Length == 0) return;

        var radius = Math.Max(3.5f, width / 90f);
        using var startPaint = new SKPaint { IsAntialias = true, Color = StartMarker, Style = SKPaintStyle.Fill };
        using var endPaint = new SKPaint { IsAntialias = true, Color = EndMarker, Style = SKPaintStyle.Fill };

        ToPixel(first.X[0], first.Y[0], extent, width, height, out var sx, out var sy);
        ToPixel(last.X[^1], last.Y[^1], extent, width, height, out var ex, out var ey);
        canvas.DrawCircle(sx, sy, radius, startPaint);
        canvas.DrawCircle(ex, ey, radius, endPaint);
    }

    private static void ToPixel(double x, double y, MapBounds extent, int width, int height, out float px, out float py)
    {
        px = (float)((x - extent.MinX) / extent.Width * width);
        py = (float)((extent.MaxY - y) / extent.Height * height);
    }
}
