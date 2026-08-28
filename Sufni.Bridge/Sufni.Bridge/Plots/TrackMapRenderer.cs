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
    private static readonly SKColor StartMarker = new(0x30, 0xC7, 0x4B);
    private static readonly SKColor FinishCheck = new(0x22, 0x22, 0x22);
    private static readonly SKColor FinishOutline = new(0x33, 0x33, 0x33);
    private static readonly SKColor AttributionColor = new(0xD0, 0xD0, 0xD0);
    private static readonly SKColor ScaleBackdrop = new(0x15, 0x19, 0x1C, 0xC8);
    private const string Attribution = "Esri, Maxar, Earthstar Geographics";
    // The overlay uses a stepped Turbo scale: both the colour bar and the track lines are
    // quantised to these levels, so a line colour maps back to exactly one bar block.
    private const int OverlayLevels = 12;

    public static Bitmap Render(
        SessionTrack track,
        SKBitmap? tiles,
        MapBounds? tilesBounds,
        int width,
        int height,
        ZoomWindow? highlight,
        TrackOverlay? overlay = null,
        bool drawMarkers = true,
        SKColor? plainTrackColor = null,
        MapBounds? focusBounds = null,
        bool drawDirectionArrow = false,
        bool haloTrack = false)
    {
        if (width < 1) width = 1;
        if (height < 1) height = 1;

        // focusBounds lets the caller zoom the map with the time window: it passes the bounds of
        // the zoomed sub-range instead of the whole track, and supplies a tile mosaic fetched for
        // the same extent. Without it the map always frames the complete track.
        var extent = (focusBounds ?? track.Bounds).PadAndFit(MapBounds.DefaultPad, width / (double)height);

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(DataBackground);

        if (tiles is not null && tilesBounds is not null && tilesBounds.Width > 0 && tilesBounds.Height > 0)
        {
            // Draw the sub-window of the mosaic that matches this surface's extent.
            // extent shares the mosaic's centre and is a subset of tilesBounds, so the
            // whole surface is covered by imagery and the aspect ratio is preserved.
            var sx0 = (extent.MinX - tilesBounds.MinX) / tilesBounds.Width * tiles.Width;
            var sx1 = (extent.MaxX - tilesBounds.MinX) / tilesBounds.Width * tiles.Width;
            var sy0 = (tilesBounds.MaxY - extent.MaxY) / tilesBounds.Height * tiles.Height;
            var sy1 = (tilesBounds.MaxY - extent.MinY) / tilesBounds.Height * tiles.Height;
            var src = new SKRect((float)sx0, (float)sy0, (float)sx1, (float)sy1);
            canvas.DrawBitmap(tiles, src, SKRect.Create(0, 0, width, height));
        }
        else if (tiles is not null)
        {
            canvas.DrawBitmap(tiles, SKRect.Create(0, 0, width, height));
        }

        if (!track.IsEmpty && extent.Width > 0 && extent.Height > 0)
        {
            // +50% over the original width: the halo margin below stays absolute, so a thicker
            // line means proportionally less white and more colour inside the zoom window.
            var strokeWidth = Math.Max(3f, width / 120f);
            if (overlay is not null)
                DrawOverlay(canvas, track, overlay, extent, width, height, strokeWidth);
            else
            {
                if (haloTrack)
                {
                    // Thin white casing along the whole track. Half the width of the zoom halo, so
                    // where both exist the zoomed sub-range still reads as the thicker outline.
                    using var casing = Stroke(HighlightLine, strokeWidth + HaloWidth(width));
                    foreach (var segment in track.Segments)
                        DrawSegment(canvas, segment, extent, width, height, casing, null);
                }

                using var muted = Stroke(plainTrackColor ?? TrackLine, strokeWidth);
                foreach (var segment in track.Segments)
                    DrawSegment(canvas, segment, extent, width, height, muted, null);
            }

            if (highlight is not null && highlight.EndSeconds > highlight.StartSeconds)
            {
                // White casing under the zoomed sub-range: the line keeps its metric colour
                // but gains an outline that marks the zoomed region. Edge-overlap selection
                // (draw an edge when it overlaps the window) mirrors DrawOverlay/DrawOverlayWindow,
                // so the halo follows the coloured line exactly — including across sparse GPS gaps,
                // where a per-point window test can leave a single in-window point and no halo.
                // Thin casing: the white margin only has to separate the line from the imagery.
                // A wider one swallows the colour that carries the actual information.
                var haloWidth = strokeWidth + Math.Max(1.5f, width / 240f);
                using var halo = Stroke(HighlightLine, haloWidth);
                DrawWindowedEdges(canvas, track, extent, width, height, halo, highlight);

                if (overlay is not null)
                    DrawOverlayWindow(canvas, track, overlay, extent, width, height, strokeWidth, highlight);
                else
                {
                    using var color = Stroke(plainTrackColor ?? TrackLine, strokeWidth);
                    DrawWindowedEdges(canvas, track, extent, width, height, color, highlight);
                }
            }

            if (drawMarkers) DrawEndpoints(canvas, track, extent, width, height);
            if (drawDirectionArrow)
                DrawDirectionArrows(canvas, track, extent, width, height, plainTrackColor ?? TrackLine);
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

    // White casing width shared by the plain track line and the direction arrows. Both end up
    // showing half of it on each side, so the arrow's outline is exactly as thick as the track's.
    private static float HaloWidth(int width) => Math.Max(0.75f, width / 480f);

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

    private static void DrawOverlayWindow(
        SKCanvas canvas,
        SessionTrack track,
        TrackOverlay overlay,
        MapBounds extent,
        int width,
        int height,
        float strokeWidth,
        ZoomWindow window)
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
                if (!ClipEdgeToWindow(segment, i, extent, width, height, window,
                        out var x0, out var y0, out var x1, out var y1))
                    continue;
                paint.Color = ColorFor(values[i], overlay.Min, overlay.Max, turbo);
                canvas.DrawLine(x0, y0, x1, y1, paint);
            }
        }
    }

    // Draws the part of the track that falls inside [window.Start, window.End] with a single paint.
    // Each edge is clipped to the window in the time domain (ClipEdgeToWindow), so the outline covers
    // exactly the zoomed time sub-range and pans smoothly with the window — even inside a single long
    // edge that spans a sparse GPS gap, where drawing whole edges would pin the halo to the gap line.
    private static void DrawWindowedEdges(
        SKCanvas canvas,
        SessionTrack track,
        MapBounds extent,
        int width,
        int height,
        SKPaint paint,
        ZoomWindow window)
    {
        foreach (var segment in track.Segments)
        {
            var edges = Math.Max(0, segment.X.Length - 1);
            for (var i = 0; i < edges; i++)
            {
                if (ClipEdgeToWindow(segment, i, extent, width, height, window,
                        out var x0, out var y0, out var x1, out var y1))
                    canvas.DrawLine(x0, y0, x1, y1, paint);
            }
        }
    }

    // Clips edge i of a segment to [window.Start, window.End] in time and returns the pixel endpoints
    // of the in-window part. Positions at the window boundaries are linearly interpolated (constant
    // speed within an edge). Returns false when the edge does not overlap the window.
    private static bool ClipEdgeToWindow(
        TrackSegment segment,
        int i,
        MapBounds extent,
        int width,
        int height,
        ZoomWindow window,
        out float x0,
        out float y0,
        out float x1,
        out float y1)
    {
        x0 = y0 = x1 = y1 = 0f;
        var t0 = segment.TimeSeconds[i];
        var t1 = segment.TimeSeconds[i + 1];
        if (t1 <= t0)
            return false;
        var a = Math.Max(t0, window.StartSeconds);
        var b = Math.Min(t1, window.EndSeconds);
        if (b <= a)
            return false;

        var f0 = (a - t0) / (t1 - t0);
        var f1 = (b - t0) / (t1 - t0);
        var dx = segment.X[i + 1] - segment.X[i];
        var dy = segment.Y[i + 1] - segment.Y[i];
        ToPixel(segment.X[i] + dx * f0, segment.Y[i] + dy * f0, extent, width, height, out x0, out y0);
        ToPixel(segment.X[i] + dx * f1, segment.Y[i] + dy * f1, extent, width, height, out x1, out y1);
        return true;
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
        var color = turbo.GetColor(LevelCenter(frac));
        return new SKColor(color.R, color.G, color.B);
    }

    // Maps a 0..1 fraction to the centre of its quantisation level, so ColorFor and the colour
    // bar pick the same 12 colours.
    private static double LevelCenter(double frac)
    {
        var level = (int)(frac * OverlayLevels);
        if (level >= OverlayLevels) level = OverlayLevels - 1;
        if (level < 0) level = 0;
        return (level + 0.5) / OverlayLevels;
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

        // Bar and both labels together occupy the left half of the map; the right half stays clear
        // of the legend so the track keeps room.
        var barLeft = margin + minWidth + gap;
        var barLength = width * 0.5f - margin - minWidth - maxWidth - 2f * gap;
        if (barLength < 24f)
            return;
        var barRight = barLeft + barLength;

        var barTop = margin;
        var backdrop = new SKRect(
            margin - 4f,
            barTop - 4f,
            barRight + gap + maxWidth + 4f,
            barTop + barHeight + 4f);
        using var backdropPaint = new SKPaint
        {
            IsAntialias = true,
            Color = ScaleBackdrop,
            Style = SKPaintStyle.Fill
        };
        canvas.DrawRoundRect(backdrop, 4f, 4f, backdropPaint);

        // Discrete blocks instead of a gradient: a colour read off the track can be matched to one
        // block, which a continuous ramp does not allow.
        var turbo = new ScottPlot.Colormaps.Turbo();
        using var blockPaint = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill };
        for (var i = 0; i < OverlayLevels; i++)
        {
            var color = turbo.GetColor((i + 0.5) / OverlayLevels);
            blockPaint.Color = new SKColor(color.R, color.G, color.B);
            var left = barLeft + barLength * i / OverlayLevels;
            var right = barLeft + barLength * (i + 1) / OverlayLevels;
            // Half a pixel of overlap keeps hairline gaps out of the bar on fractional widths.
            canvas.DrawRect(left, barTop, right - left + 0.5f, barHeight, blockPaint);
        }

        var textY = barTop + barHeight - (barHeight - textSize) * 0.35f;
        canvas.DrawText(minLabel, margin, textY, textPaint);
        canvas.DrawText(maxLabel, barRight + gap, textY, textPaint);
    }

    // One arrow per segment, at half of that segment's travelled distance, pointing the way the
    // rider went. A segment is one sub-session (one WallClockSlice), so a combined session gets an
    // arrow per run instead of a single one for the whole set. Distance-based rather than
    // time-based so a long stop does not drag the marker onto the spot where the rider stood still.
    private static void DrawDirectionArrows(
        SKCanvas canvas,
        SessionTrack track,
        MapBounds extent,
        int width,
        int height,
        SKColor fill)
    {
        var size = Math.Max(5f, width / 48f);
        var casingWidth = HaloWidth(width);
        foreach (var segment in track.Segments)
            DrawSegmentArrow(canvas, segment, extent, width, height, size, fill, casingWidth);
    }

    private static void DrawSegmentArrow(
        SKCanvas canvas,
        TrackSegment segment,
        MapBounds extent,
        int width,
        int height,
        float size,
        SKColor fill,
        float casingWidth)
    {
        static double EdgeLength(TrackSegment s, int i)
        {
            var dx = s.X[i + 1] - s.X[i];
            var dy = s.Y[i + 1] - s.Y[i];
            return Math.Sqrt(dx * dx + dy * dy);
        }

        var total = 0.0;
        for (var i = 0; i + 1 < segment.X.Length; i++)
            total += EdgeLength(segment, i);
        if (total <= 0.0)
            return;

        var target = total * 0.5;
        var walked = 0.0;
        for (var i = 0; i + 1 < segment.X.Length; i++)
        {
            var length = EdgeLength(segment, i);
            if (length <= 0.0)
                continue;
            if (walked + length >= target)
            {
                var f = (target - walked) / length;
                var dx = segment.X[i + 1] - segment.X[i];
                var dy = segment.Y[i + 1] - segment.Y[i];
                ToPixel(segment.X[i] + dx * f, segment.Y[i] + dy * f, extent, width, height, out var px, out var py);
                ToPixel(segment.X[i], segment.Y[i], extent, width, height, out var ax, out var ay);
                ToPixel(segment.X[i + 1], segment.Y[i + 1], extent, width, height, out var bx, out var by);
                DrawArrowHead(canvas, px, py, bx - ax, by - ay, size, fill, casingWidth);
                return;
            }

            walked += length;
        }
    }

    // Arrowhead with a notched back — the shape used on trail maps. Filled in the track colour and
    // cased in white: the white is stroked first and the fill painted over it, so the casing sits
    // outside the shape instead of eating into it.
    private static void DrawArrowHead(
        SKCanvas canvas, float cx, float cy, float dx, float dy, float size, SKColor fill, float casingWidth)
    {
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length <= 0.0001f)
            return;

        var ux = dx / length;
        var uy = dy / length;
        var nx = -uy;
        var ny = ux;

        SKPoint At(float along, float across) =>
            new(cx + ux * along + nx * across, cy + uy * along + ny * across);

        using var path = new SKPath();
        // Blunt head: wider than long (1.24 x 1.70 in units of size), matching a trail-map arrow.
        // A longer, narrower head reads as a pointer and draws more attention than it should.
        path.MoveTo(At(size * 0.62f, 0f));
        path.LineTo(At(-size * 0.62f, size * 0.85f));
        path.LineTo(At(-size * 0.30f, 0f));
        path.LineTo(At(-size * 0.62f, -size * 0.85f));
        path.Close();

        using var casing = new SKPaint
        {
            IsAntialias = true,
            Color = SKColors.White,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = casingWidth,
            StrokeJoin = SKStrokeJoin.Round,
            StrokeCap = SKStrokeCap.Round
        };
        using var body = new SKPaint { IsAntialias = true, Color = fill, Style = SKPaintStyle.Fill };
        canvas.DrawPath(path, casing);
        canvas.DrawPath(path, body);
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

        var radius = Math.Max(4f, width / 80f);
        var ringWidth = Math.Max(1.5f, radius * 0.28f);

        ToPixel(first.X[0], first.Y[0], extent, width, height, out var sx, out var sy);
        ToPixel(last.X[^1], last.Y[^1], extent, width, height, out var ex, out var ey);

        // Start: green dot with a white ring (Strava-style).
        using var startFill = new SKPaint { IsAntialias = true, Color = StartMarker, Style = SKPaintStyle.Fill };
        using var whiteRing = new SKPaint
        {
            IsAntialias = true, Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = ringWidth
        };
        canvas.DrawCircle(sx, sy, radius, startFill);
        canvas.DrawCircle(sx, sy, radius, whiteRing);

        // Finish: white badge with a checkered-flag pattern.
        DrawFinishFlag(canvas, ex, ey, radius * 1.3f);
    }

    private static void DrawFinishFlag(SKCanvas canvas, float cx, float cy, float radius)
    {
        using var badge = new SKPaint { IsAntialias = true, Color = SKColors.White, Style = SKPaintStyle.Fill };
        canvas.DrawCircle(cx, cy, radius, badge);

        // Checkerboard, clipped to an inner circle so it stays inside the badge.
        var inner = radius * 0.82f;
        canvas.Save();
        using (var clip = new SKPath())
        {
            clip.AddCircle(cx, cy, inner);
            canvas.ClipPath(clip, antialias: true);
        }

        const int n = 4;
        var cell = inner * 2f / n;
        var ox = cx - inner;
        var oy = cy - inner;
        using var check = new SKPaint { Color = FinishCheck, Style = SKPaintStyle.Fill, IsAntialias = false };
        for (var row = 0; row < n; row++)
        {
            for (var col = 0; col < n; col++)
            {
                if (((row + col) & 1) != 0) continue;
                canvas.DrawRect(ox + col * cell, oy + row * cell, cell, cell, check);
            }
        }

        canvas.Restore();

        // Thin outline so the white badge separates from the map.
        using var outline = new SKPaint
        {
            IsAntialias = true, Color = FinishOutline, Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1f, radius * 0.12f)
        };
        canvas.DrawCircle(cx, cy, radius, outline);
    }

    private static void ToPixel(double x, double y, MapBounds extent, int width, int height, out float px, out float py)
    {
        px = (float)((x - extent.MinX) / extent.Width * width);
        py = (float)((extent.MaxY - y) / extent.Height * height);
    }
}
