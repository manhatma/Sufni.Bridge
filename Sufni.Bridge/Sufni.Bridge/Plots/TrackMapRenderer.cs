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
    // Thin dark separator between the coloured line and its white casing inside the zoom
    // window. Pure white against a bright colour is a weak edge; the dark line makes the
    // zoomed stretch stand out on any imagery.
    private static readonly SKColor ZoomOutline = new(0x10, 0x12, 0x14);
    private const string Attribution = "Esri, Maxar, Earthstar Geographics";
    // The overlay uses a stepped Turbo scale: both the colour bar and the track lines are
    // quantised to these levels, so a line colour maps back to exactly one bar block.
    private const int OverlayLevels = 12;

    public static MapBounds ExtentFor(SessionTrack track, MapBounds? focusBounds, int width, int height)
    {
        if (width < 1) width = 1;
        if (height < 1) height = 1;
        // focusBounds lets the caller zoom the map with the time window: it passes the bounds of
        // the zoomed sub-range instead of the whole track, and supplies a tile mosaic fetched for
        // the same extent. Without it the map always frames the complete track.
        return (focusBounds ?? track.Bounds).PadAndFit(MapBounds.DefaultPad, width / (double)height);
    }

    public static void ProjectToPixel(
        double mapX, double mapY, MapBounds extent, int width, int height, out float px, out float py)
        => ToPixel(mapX, mapY, extent, width, height, out px, out py);

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
        bool haloTrack = false,
        bool drawStatMarkers = false,
        // Die Misc-Karte füllt den Bildschirm und verträgt kräftigere Linien; die kleine
        // Summary-Vorschau würde damit zulaufen, deshalb behält sie die schmalere Grundbreite.
        bool emphasizedLines = false)
    {
        if (width < 1) width = 1;
        if (height < 1) height = 1;

        var extent = ExtentFor(track, focusBounds, width, height);

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
            // Base width is the previous (narrow) round. The extra 30% applies only to the
            // emphasized map; the halo margin below stays absolute, so a thicker line means
            // proportionally less white and more colour inside the zoom window.
            var strokeWidth = StrokeWidthFor(width, emphasizedLines);
            if (overlay is not null)
                DrawOverlay(canvas, track, overlay, extent, width, height, strokeWidth);
            else
            {
                if (haloTrack)
                {
                    // Thin white casing along the whole track. Half the width of the zoom halo, so
                    // where both exist the zoomed sub-range still reads as the thicker outline.
                    using var casing = Stroke(HighlightLine, strokeWidth + HaloWidth(width, emphasizedLines));
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
                // The white casing is now wider and a thin dark separator sits between it and the
                // coloured line, so the zoomed stretch reads as white / black / colour from the
                // outside in.
                // Casing widths are diameters: half of the extra width shows on each side.
                var outlineMargin = Math.Max(0.9f, width / 400f);
                var haloMargin = ZoomHaloMargin(width, emphasizedLines) * 1.6f;
                using var halo = Stroke(HighlightLine, strokeWidth + outlineMargin + haloMargin);
                DrawWindowedEdges(canvas, track, extent, width, height, halo, highlight);
                using var outline = Stroke(ZoomOutline, strokeWidth + outlineMargin);
                DrawWindowedEdges(canvas, track, extent, width, height, outline, highlight);

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
                DrawDirectionArrows(canvas, track, extent, width, height, plainTrackColor ?? TrackLine, emphasizedLines);

            if (overlay is not null && drawStatMarkers)
                DrawStatMarkers(canvas, track, overlay, extent, width, height, highlight, emphasizedLines);
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
        // The credit sits at the bottom edge; the Misc page control bar keeps enough distance there.
        canvas.DrawText(Attribution, width - 6 - textWidth, height - 6, textPaint);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream();
        data.SaveTo(stream);
        stream.Position = 0;
        return new Bitmap(stream);
    }

    private static float StrokeWidthFor(int width, bool emphasized) =>
        emphasized ? Math.Max(3.9f, width / 92f) : Math.Max(3f, width / 120f);
    private static float ZoomHaloMargin(int width, bool emphasized) =>
        emphasized ? Math.Max(1.65f, width / 218f) : Math.Max(1.5f, width / 240f);
    // White casing width shared by the plain track line and the direction arrows. Both end up
    // showing half of it on each side, so the arrow's outline is exactly as thick as the track's.
    private static float HaloWidth(int width, bool emphasized) =>
        emphasized ? Math.Max(0.83f, width / 436f) : Math.Max(0.75f, width / 480f);

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

    // Same overlap test as ClipEdgeToWindow / DrawOverlayWindow, without computing clipped pixels.
    private static bool EdgeOverlapsWindow(TrackSegment segment, int i, ZoomWindow window)
    {
        var t0 = segment.TimeSeconds[i];
        var t1 = segment.TimeSeconds[i + 1];
        if (t1 <= t0)
            return false;
        var a = Math.Max(t0, window.StartSeconds);
        var b = Math.Min(t1, window.EndSeconds);
        return b > a;
    }

    public readonly record struct StatEdgeRef(int SegmentIndex, int EdgeIndex, double Value);

    public readonly record struct StatMarkerLayout(
        float DotX,
        float DotY,
        float Radius,
        float LeaderStartX,
        float LeaderStartY,
        float LeaderX,
        float LeaderY,
        string Text,
        float TextSize,
        float LabelLeft,
        float LabelBaseline,
        SKRect Label,
        SKRect Bounds);

    // Die Methode wird auch vom Data-Marker gebraucht, damit er den Statistik-Fahnen
    // ausweichen kann.
    public static (StatEdgeRef? Max, StatEdgeRef? Min) FindStatEdges(
        SessionTrack track, TrackOverlay overlay, ZoomWindow? highlight)
    {
        var restrict = highlight is not null && highlight.EndSeconds > highlight.StartSeconds;

        var maxSeg = -1;
        var maxEdge = -1;
        var maxValue = double.NegativeInfinity;
        var minSeg = -1;
        var minEdge = -1;
        var minValue = double.PositiveInfinity;

        var segmentCount = Math.Min(track.Segments.Count, overlay.SegmentPairValues.Count);
        for (var s = 0; s < segmentCount; s++)
        {
            var segment = track.Segments[s];
            var values = overlay.SegmentPairValues[s];
            var edges = Math.Min(values.Length, Math.Max(0, segment.X.Length - 1));
            for (var i = 0; i < edges; i++)
            {
                if (restrict && !EdgeOverlapsWindow(segment, i, highlight!))
                    continue;
                var v = values[i];
                if (!double.IsFinite(v)) continue;
                if (v > maxValue)
                {
                    maxValue = v;
                    maxSeg = s;
                    maxEdge = i;
                }

                if (v < minValue)
                {
                    minValue = v;
                    minSeg = s;
                    minEdge = i;
                }
            }
        }

        if (maxSeg < 0)
            return (null, null);

        var maxRef = new StatEdgeRef(maxSeg, maxEdge, maxValue);
        // Liegt Max und Min auf derselben Kante, den Min-Marker weglassen — wie bisher.
        StatEdgeRef? minRef = minSeg >= 0 && (minSeg != maxSeg || minEdge != maxEdge)
            ? new StatEdgeRef(minSeg, minEdge, minValue)
            : null;
        return (maxRef, minRef);
    }

    // The data marker in the view needs these areas so it can dodge them.
    public static (SKRect? Max, SKRect? Min) StatMarkerBounds(
        SessionTrack track, TrackOverlay overlay, MapBounds extent,
        int width, int height, ZoomWindow? highlight)
    {
        var (max, min) = FindStatEdges(track, overlay, highlight);
        SKRect? maxBounds = null;
        SKRect? minBounds = null;
        if (max is { } maxEdge)
        {
            maxBounds = LayoutStatMarker(
                track.Segments[maxEdge.SegmentIndex], maxEdge.EdgeIndex, maxEdge.Value, "max ",
                extent, width, height, +1f).Bounds;
        }

        if (min is { } minEdge)
        {
            minBounds = LayoutStatMarker(
                track.Segments[minEdge.SegmentIndex], minEdge.EdgeIndex, minEdge.Value, "min ",
                extent, width, height, -1f).Bounds;
        }

        return (maxBounds, minBounds);
    }

    private static void DrawStatMarkers(
        SKCanvas canvas,
        SessionTrack track,
        TrackOverlay overlay,
        MapBounds extent,
        int width,
        int height,
        ZoomWindow? highlight,
        bool emphasizedLines)
    {
        var turbo = new ScottPlot.Colormaps.Turbo();
        var (max, min) = FindStatEdges(track, overlay, highlight);
        if (max is not { } maxEdge)
            return;

        DrawStatMarker(canvas, track.Segments[maxEdge.SegmentIndex], maxEdge.EdgeIndex, maxEdge.Value, "max ",
            overlay, extent, width, height, turbo, +1f, emphasizedLines);

        if (min is { } minEdge)
        {
            DrawStatMarker(canvas, track.Segments[minEdge.SegmentIndex], minEdge.EdgeIndex, minEdge.Value, "min ",
                overlay, extent, width, height, turbo, -1f, emphasizedLines);
        }
    }

    private static StatMarkerLayout LayoutStatMarker(
        TrackSegment segment, int edge, double value, string prefix,
        MapBounds extent, int width, int height, float side)
    {
        ToPixel(segment.X[edge], segment.Y[edge], extent, width, height, out var x0, out var y0);
        ToPixel(segment.X[edge + 1], segment.Y[edge + 1], extent, width, height, out var x1, out var y1);
        var cx = (x0 + x1) * 0.5f;
        var cy = (y0 + y1) * 0.5f;

        var dx = x1 - x0; var dy = y1 - y0;
        var len = MathF.Sqrt(dx * dx + dy * dy);
        if (len <= 0f) { dx = 1f; dy = 0f; len = 1f; }
        dx /= len; dy /= len;
        // Normal to the travel direction. side flips it onto the other side of the line
        // so Max and Min never point the same way.
        var nx = -dy * side;
        var ny =  dx * side;

        var radius = Math.Max(2.5f, width / 110f);
        var leader = Math.Max(14f, width / 16f);
        var startX = cx + nx * radius * 1.4f;
        var startY = cy + ny * radius * 1.4f;
        var endX   = cx + nx * leader;
        var endY   = cy + ny * leader;

        var text = prefix + FormatScale(value);
        var textSize = Math.Max(10f, width / 48f + 1f);
        using var measure = new SKPaint { TextSize = textSize };
        var textWidth = measure.MeasureText(text);
        const float pad = 3f;
        var labelLeft = nx >= 0 ? endX + 3f : endX - 3f - textWidth;
        var labelBaseline = endY + textSize * 0.35f;
        if (width >= textWidth + 8f)
            labelLeft = Math.Clamp(labelLeft, 4f, width - textWidth - 4f);
        var minBaseline = textSize + pad + 4f;
        var maxBaseline = height - pad - 4f;
        if (maxBaseline >= minBaseline)
            labelBaseline = Math.Clamp(labelBaseline, minBaseline, maxBaseline);
        var backdrop = new SKRect(
            labelLeft - pad,
            labelBaseline - textSize - pad,
            labelLeft + textWidth + pad,
            labelBaseline + pad);

        var dot = SKRect.Create(cx - radius, cy - radius, 2f * radius, 2f * radius);
        var leaderRect = SKRect.Create(
            Math.Min(startX, endX), Math.Min(startY, endY),
            Math.Abs(endX - startX), Math.Abs(endY - startY));
        var bounds = SKRect.Union(SKRect.Union(dot, leaderRect), backdrop);
        return new StatMarkerLayout(
            cx, cy, radius, startX, startY, endX, endY,
            text, textSize, labelLeft, labelBaseline, backdrop, bounds);
    }

    private static void DrawStatMarker(
        SKCanvas canvas,
        TrackSegment segment,
        int edge,
        double value,
        string prefix,
        TrackOverlay overlay,
        MapBounds extent,
        int width,
        int height,
        ScottPlot.Colormaps.Turbo turbo,
        float side,
        bool emphasizedLines)
    {
        var layout = LayoutStatMarker(segment, edge, value, prefix, extent, width, height, side);

        using var fill = new SKPaint
        {
            IsAntialias = true,
            Color = ColorFor(value, overlay.Min, overlay.Max, turbo),
            Style = SKPaintStyle.Fill
        };
        using var ring = new SKPaint
        {
            IsAntialias = true,
            Color = HighlightLine,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1f, HaloWidth(width, emphasizedLines) * 1.5f)
        };

        using var leaderPaint = new SKPaint
        {
            IsAntialias = true,
            Color = HighlightLine,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1f, HaloWidth(width, emphasizedLines) * 1.5f),
            StrokeCap = SKStrokeCap.Round
        };
        canvas.DrawLine(layout.LeaderStartX, layout.LeaderStartY, layout.LeaderX, layout.LeaderY, leaderPaint);

        canvas.DrawCircle(layout.DotX, layout.DotY, layout.Radius, fill);
        canvas.DrawCircle(layout.DotX, layout.DotY, layout.Radius, ring);

        using var textPaint = new SKPaint
        {
            IsAntialias = true,
            Color = AttributionColor,
            TextSize = layout.TextSize
        };
        using var backdropPaint = new SKPaint
        {
            IsAntialias = true,
            Color = ScaleBackdrop,
            Style = SKPaintStyle.Fill
        };
        canvas.DrawRoundRect(layout.Label, 4f, 4f, backdropPaint);
        canvas.DrawText(layout.Text, layout.LabelLeft, layout.LabelBaseline, textPaint);
    }

    // The data marker in the view paints its dot in the colour of the edge it sits on, so it
    // needs the same quantised lookup the line itself uses.
    public static SKColor OverlayColorFor(TrackOverlay overlay, double value)
        => ColorFor(value, overlay.Min, overlay.Max, new ScottPlot.Colormaps.Turbo());

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
        var textSize = Math.Max(10f, width / 48f + 1f);
        using var textPaint = new SKPaint
        {
            IsAntialias = true,
            Color = AttributionColor,
            TextSize = textSize
        };

        var minLabel = FormatScale(overlay.Min);
        // Das Zeichen zeigt an, dass oberhalb dieses Werts noch Daten liegen, die Skala dort
        // aber endet.
        var maxLabel = $"{(overlay.MaxIsClamped ? "≥ " : "")}{FormatScale(overlay.Max)} {overlay.Unit}";
        var minWidth = textPaint.MeasureText(minLabel);
        var maxWidth = textPaint.MeasureText(maxLabel);

        // Same visual inset as the control bar and the aggregate pill, which sit 12 pt away from
        // the map edge. The Misc map is rendered 620 px wide and shown about 373 pt wide, so one
        // point is about 1.66 bitmap pixels and 12 pt land at width / 31.
        var margin = Math.Max(8f, width / 31f);
        var gap = Math.Max(6f, width / 80f);
        var barHeight = Math.Max(20f, width / 24f);

        // Bar and both labels together occupy the left half of the map; the right half stays clear
        // of the legend so the track keeps room.
        var barLeft = margin + minWidth + gap;
        var barLength = width * 0.5f - margin - minWidth - maxWidth - 2f * gap;
        if (barLength < 24f)
            return;
        var barRight = barLeft + barLength;

        var barTop = margin;
        var tickSize = Math.Max(8f, textSize * 0.8f);
        using var tickPaint = new SKPaint
        {
            IsAntialias = true,
            Color = AttributionColor,
            TextSize = tickSize
        };

        // Tick labels at every third level boundary (i = 3, 6, 9). Drop them when the bar is too
        // narrow for four equal slots (min/max plus the three ticks share the length visually).
        var tickIndices = new[] { 3, 6, 9 };
        var tickLabels = new string[tickIndices.Length];
        var widestTick = 0f;
        for (var t = 0; t < tickIndices.Length; t++)
        {
            var i = tickIndices[t];
            var value = overlay.Min + (overlay.Max - overlay.Min) * i / OverlayLevels;
            tickLabels[t] = FormatScale(value);
            var w = tickPaint.MeasureText(tickLabels[t]);
            if (w > widestTick) widestTick = w;
        }

        var showTicks = barLength / 4f >= widestTick + 6f;
        var backdropBottom = barTop + barHeight + (showTicks ? 2f + tickSize : 0f) + 4f;
        var backdrop = new SKRect(
            margin - 4f,
            barTop - 4f,
            barRight + gap + maxWidth + 4f,
            backdropBottom);
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

        var textY = barTop + barHeight * 0.5f + textSize * 0.36f;
        canvas.DrawText(minLabel, margin, textY, textPaint);
        canvas.DrawText(maxLabel, barRight + gap, textY, textPaint);

        if (!showTicks)
            return;

        using var tickStroke = new SKPaint
        {
            IsAntialias = true,
            Color = AttributionColor,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f
        };
        var tickBaseline = barTop + barHeight + 2f + tickSize;
        for (var t = 0; t < tickIndices.Length; t++)
        {
            var i = tickIndices[t];
            var x = barLeft + barLength * i / OverlayLevels;
            canvas.DrawLine(x, barTop + barHeight, x, barTop + barHeight + 3f, tickStroke);
            var labelWidth = tickPaint.MeasureText(tickLabels[t]);
            canvas.DrawText(tickLabels[t], x - labelWidth * 0.5f, tickBaseline, tickPaint);
        }
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
        SKColor fill,
        bool emphasizedLines)
    {
        var size = Math.Max(5f, width / 48f);
        var casingWidth = HaloWidth(width, emphasizedLines);
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
