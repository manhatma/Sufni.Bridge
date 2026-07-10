using System;
using System.Collections.Generic;
using System.Linq;
using ScottPlot;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

public class TelemetryPlot(Plot plot) : SufniPlot(plot)
{
    protected Color FrontColor = Color.FromHex("#3288bd");
    protected Color RearColor = Color.FromHex("#66c2a5");

    // Approximate SVG render width (in px) used for the on-screen plots that call
    // AddAirtimeOverlays — matches SessionViewModel's `width` passed to GetSvgXml (derived from
    // LastKnownBounds, whose default matches the iPhone 15 Pro logical width). Only used for a
    // conservative label-collision estimate below, not for actual layout.
    private const double PlotSvgRenderWidthPx = 393;

    // Matches the Fixed PixelPadding(55, 14, 50, 40) used by the time-history plots; height is
    // SessionViewModel's `height` (b.Height / 2, default bounds 393x700 → 350 px).
    private const double PlotPixelPaddingLeft = 55;
    private const double PlotPixelPaddingRight = 14;
    private const double PlotSvgRenderHeightPx = 350;
    private const double PlotPixelPaddingBottom = 50;
    private const double PlotPixelPaddingTop = 40;

    public virtual void LoadTelemetryData(TelemetryData telemetryData) { }

    /// <summary>
    /// Draws a translucent red rectangle plus a duration label for every airtime interval,
    /// staggering labels across up to six vertical levels so closely-spaced jumps don't get
    /// overlapping text. Assumes an inverted Y-axis (yTop=0 at the top, yBottom at the bottom
    /// edge): level 0 sits 1 em above the bottom edge, further levels stack upward.
    /// </summary>
    protected void AddAirtimeOverlays(Airtime[]? airtimes, double yTop, double yBottom, double maxDuration,
        bool withLabels = true, double fillAlpha = 0.2, double minWidthPx = 0)
    {
        if (airtimes is not { Length: > 0 } || maxDuration <= 0) return;

        var plotDataWidthPx = PlotSvgRenderWidthPx - PlotPixelPaddingLeft - PlotPixelPaddingRight;

        // Matches the axis tick label size (SufniPlot) minus 2, bold for contrast on the band.
        const int fontSize = 10;

        // Label rows in data coordinates, derived from the px geometry: MiddleCenter anchor,
        // so 1 em gap to the bottom edge plus half a line height; rows step one line + gap up.
        var plotDataHeightPx = PlotSvgRenderHeightPx - PlotPixelPaddingBottom - PlotPixelPaddingTop;
        var pxToData = (yBottom - yTop) / plotDataHeightPx;
        var labelY0 = yBottom - fontSize * 1.5 * pxToData;
        var labelStep = (fontSize + 6) * pxToData;

        const int levelCount = 6;
        var levelLastLabelEnd = new double[levelCount];
        for (var k = 0; k < levelCount; k++) levelLastLabelEnd[k] = double.NegativeInfinity;

        foreach (var airtime in airtimes.OrderBy(a => a.Start))
        {
            var start = airtime.Start;
            var end = airtime.End;
            // Widen very short bands to a minimum on-screen width so brief jumps stay visible in the
            // dense full-session overview (mini-map), where they're used for navigation.
            if (minWidthPx > 0 && plotDataWidthPx > 0)
            {
                var minWidthSeconds = minWidthPx / plotDataWidthPx * maxDuration;
                if (end - start < minWidthSeconds)
                {
                    var mid = (start + end) / 2.0;
                    start = mid - minWidthSeconds / 2.0;
                    end = mid + minWidthSeconds / 2.0;
                }
            }

            var rect = Plot.Add.Rectangle(start, end, yTop, yBottom);
            rect.FillStyle.Color = Color.FromHex("#d53e4f").WithAlpha(fillAlpha);
            // Fill only — no outline. Zero the width and use a transparent colour in addition to
            // IsVisible=false so no hairline edge is drawn on the band's left/right sides.
            rect.LineStyle.Width = 0;
            rect.LineStyle.Color = Colors.Transparent;
            rect.LineStyle.IsVisible = false;

            // Session-overview mini-map draws bands only (no duration labels) for navigation.
            if (!withLabels)
                continue;

            var text = $"{airtime.End - airtime.Start:F1} s";
            var center = (airtime.Start + airtime.End) / 2;

            // Conservative estimate of the label's on-screen width, converted back to data
            // (seconds) coordinates so it can be compared against neighbouring label centers.
            var labelWidthSeconds = text.Length * fontSize * 0.6 / plotDataWidthPx * maxDuration;
            var halfWidth = labelWidthSeconds / 2.0;

            var level = -1;
            for (var k = 0; k < levelCount; k++)
            {
                if (levelLastLabelEnd[k] <= center - halfWidth)
                {
                    level = k;
                    break;
                }
            }
            if (level == -1)
            {
                // All levels collide — fall back to the one whose last label ends furthest
                // to the left (least overlap).
                level = 0;
                for (var k = 1; k < levelCount; k++)
                    if (levelLastLabelEnd[k] < levelLastLabelEnd[level]) level = k;
            }

            levelLastLabelEnd[level] = center + halfWidth;

            var label = Plot.Add.Text(text, center, labelY0 - level * labelStep);
            label.LabelFontColor = Color.FromHex("#fefefe");
            label.LabelFontSize = fontSize;
            label.LabelBold = true;
            label.LabelAlignment = Alignment.MiddleCenter;
        }
    }

    /// <summary>
    /// Minimal "travel position" colour key for the stacked velocity histograms: a thin vertical
    /// gradient strip of the bin palette at the inner-left edge, captioned "Travel", running from
    /// 0% (bottom = topped out) to 100% (top = bottomed out). Drawn in data coordinates from the
    /// given axis extents so it sits where the histogram bars are short.
    /// </summary>
    protected void AddBinColorLegend(IReadOnlyList<Color> palette, double xLeft, double xRight, double yTop)
    {
        var xRange = xRight - xLeft;
        if (xRange <= 0 || yTop <= 0 || palette.Count == 0) return;

        var width = xRange * 0.03;
        var x = xLeft + xRange * 0.06;
        var y0 = yTop * 0.30;
        var y1 = yTop * 0.70;
        var dy = (y1 - y0) / palette.Count;

        for (var k = 0; k < palette.Count; k++)
        {
            Plot.Add.Bar(new Bar
            {
                Position = x,
                ValueBase = y0 + k * dy,
                Value = y0 + (k + 1) * dy,
                FillColor = palette[k],
                LineColor = Colors.Black,
                LineWidth = 0.3f,
                Orientation = Orientation.Vertical,
                Size = width,
            });
        }

        var caption = Plot.Add.Text("Travel", x, y1 + dy * 1.2);
        caption.LabelFontColor = Color.FromHex("#dddddd");
        caption.LabelFontSize = 8;
        caption.LabelFontName = "Menlo";
        caption.LabelAlignment = Alignment.LowerCenter;
        caption.LabelBold = true;

        void EndLabel(string text, double y)
        {
            var t = Plot.Add.Text(text, x + width / 2.0, y);
            t.LabelFontColor = Color.FromHex("#dddddd");
            t.LabelFontSize = 8;
            t.LabelFontName = "Menlo";
            t.LabelAlignment = Alignment.MiddleLeft;
            t.LabelOffsetX = 2;
            t.LabelBold = true;
        }

        EndLabel("100%", y1);
        EndLabel("0%", y0);
    }

    /// <summary>
    /// Resolves an optional [start,end] second-window against the data, falling back to the full
    /// range for a degenerate request (narrower than 1e-6 s, or than one sample once snapped).
    /// Returns the clamped seconds, the sample sub-range [s0,s1), and whether a window applies.
    /// Shared by the combined front+rear time-series plots.
    /// </summary>
    protected static (double winStart, double winEnd, int s0, int s1, bool hasWindow) ResolveTimeWindow(
        double? windowStartSeconds, double? windowEndSeconds, double maxDuration, int sampleRate, int length)
    {
        if (windowStartSeconds is null || windowEndSeconds is null)
            return (0, maxDuration, 0, length, false);

        var winStart = Math.Clamp(windowStartSeconds.Value, 0, maxDuration);
        var winEnd = Math.Clamp(windowEndSeconds.Value, winStart, maxDuration);
        if (winEnd - winStart < 1e-6)
            return (0, maxDuration, 0, length, false);

        var s0 = Math.Clamp((int)Math.Floor(winStart * sampleRate), 0, length);
        var s1 = Math.Clamp((int)Math.Ceiling(winEnd * sampleRate), 0, length);
        if (s1 <= s0)
            return (0, maxDuration, 0, length, false);

        return (winStart, winEnd, s0, s1, true);
    }

    /// <summary>Scans a (possibly null) array over [s0,s1) updating running max/min.</summary>
    protected static void ScanMinMax(double[]? a, int s0, int s1, ref double max, ref double min)
    {
        if (a is null) return;
        var hi = Math.Min(s1, a.Length);
        for (int i = Math.Min(s0, a.Length); i < hi; i++)
        {
            if (a[i] > max) max = a[i];
            if (a[i] < min) min = a[i];
        }
    }

    /// <summary>Formats a window edge time as m:ss.s (mirrors CropPageViewModel.FormatTime).</summary>
    protected static string FormatWindowTime(double seconds)
    {
        var minutes = (int)(seconds / 60);
        var secs = seconds % 60;
        return $"{minutes}:{secs:00.0}";
    }

    /// <summary>Compact upper-left Front/Rear colour key for the combined (front+rear) time plots.</summary>
    protected void AddCombinedLegend(bool hasFront, bool hasRear, double x, double yTop)
    {
        if (hasFront)
        {
            var l = Plot.Add.Text("Front", x, yTop);
            l.LabelFontColor = FrontColor;
            l.LabelFontSize = 11;
            l.LabelBold = true;
            l.LabelAlignment = Alignment.UpperLeft;
            l.LabelOffsetX = 6;
            l.LabelOffsetY = 6;
        }
        if (hasRear)
        {
            var l = Plot.Add.Text("Rear", x, yTop);
            l.LabelFontColor = RearColor;
            l.LabelFontSize = 11;
            l.LabelBold = true;
            l.LabelAlignment = Alignment.UpperLeft;
            l.LabelOffsetX = 6;
            l.LabelOffsetY = 22;
        }
    }

    /// <summary>Per-side max/min/rms over the sample sub-range [s0,s1), or null if absent/empty.</summary>
    protected static (double Max, double Min, double Rms)? ComputeSideStats(double[]? a, int s0, int s1)
    {
        if (a is null) return null;
        var lo = Math.Min(s0, a.Length);
        var hi = Math.Min(s1, a.Length);
        if (hi <= lo) return null;
        double max = double.NegativeInfinity, min = double.PositiveInfinity, sumSq = 0;
        for (int i = lo; i < hi; i++)
        {
            var x = a[i];
            if (x > max) max = x;
            if (x < min) min = x;
            sumSq += x * x;
        }
        return (max, min, Math.Sqrt(sumSq / (hi - lo)));
    }

    /// <summary>
    /// Compact single-colour (gold) stats box for the combined front+rear time plots: rows max/min/rms,
    /// columns headed "front"/"rear" (whichever sides are present), CENTRED over their right-aligned
    /// numeric columns. The header line starts with the unit (a non-whitespace char) so the SVG renderer
    /// does not trim the leading padding and shift the header left. Anchored upper-right at (x, yTop).
    /// </summary>
    protected void AddCombinedStatsBox((double Max, double Min, double Rms)? front,
        (double Max, double Min, double Rms)? rear, string unit, double x, double yTop)
    {
        var hasF = front.HasValue;
        var hasR = rear.HasValue;
        if (!hasF && !hasR) return;

        const int minCol = 7;
        const string sep = "\u00A0";

        static string Nbsp(string s) => s.Replace(' ', '\u00A0');
        static string Ctr(string s, int w)
        {
            if (s.Length >= w) return Nbsp(s);
            var pad = w - s.Length;
            var left = pad / 2;
            return Nbsp(new string(' ', left) + s + new string(' ', pad - left));
        }
        static int MaxLen(string[] a)
        {
            var m = 0;
            foreach (var s in a)
                if (s.Length > m) m = s.Length;
            return m;
        }

        var fv = hasF
            ? new[] { front!.Value.Max.ToString("F3"), front.Value.Min.ToString("F3"), front.Value.Rms.ToString("F3") }
            : Array.Empty<string>();
        var rv = hasR
            ? new[] { rear!.Value.Max.ToString("F3"), rear.Value.Min.ToString("F3"), rear.Value.Rms.ToString("F3") }
            : Array.Empty<string>();

        // The front column reserves one character more than its widest number, so a long negative
        // value never butts against the "min:" label. The rear column only has to fit its own
        // numbers \u2014 `sep` supplies the gap to the front column.
        var fw = hasF ? Math.Max(minCol, MaxLen(fv) + 1) : 0;
        var rw = hasR ? Math.Max(minCol, MaxLen(rv)) : 0;

        // Corner cell = unit, padded to the "max:" label-column width. Starts with a non-whitespace
        // character so the header row is not left-shifted by SVG whitespace trimming.
        var corner = Nbsp(unit.Length >= 4 ? unit : unit + new string(' ', 4 - unit.Length));

        var header = corner;
        if (hasF) header += Ctr("front", fw);
        if (hasF && hasR) header += sep;
        if (hasR) header += Ctr("rear", rw);

        string Row(string label, int i)
        {
            var s = label;
            if (hasF) s += Nbsp(fv[i].PadLeft(fw));
            if (hasF && hasR) s += sep;
            if (hasR) s += Nbsp(rv[i].PadLeft(rw));
            return s;
        }

        var text =
            header + "\n" +
            Row("max:", 0) + "\n" +
            Row("min:", 1) + "\n" +
            Row("rms:", 2);

        var color = Color.FromHex("#FFD700");
        var label = Plot.Add.Text(text, x, yTop);
        label.LabelFontColor = color;
        label.LabelFontSize = 9;
        label.LabelFontName = "Menlo";
        label.LabelAlignment = Alignment.UpperRight;
        label.LabelOffsetX = -10;
        label.LabelOffsetY = 6;
        label.LabelBold = true;
        label.LabelBackgroundColor = Color.FromHex("#15191C").WithAlpha(220);
        label.LabelBorderColor = color.WithAlpha(80);
        label.LabelBorderWidth = 1;
        label.LabelPadding = 5;
    }
}
