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
    protected void AddAirtimeOverlays(Airtime[]? airtimes, double yTop, double yBottom, double maxDuration)
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
            var rect = Plot.Add.Rectangle(airtime.Start, airtime.End, yTop, yBottom);
            rect.FillStyle.Color = Color.FromHex("#d53e4f").WithAlpha(0.2);
            rect.LineStyle.IsVisible = false;

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
}