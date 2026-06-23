using System.Collections.Generic;
using ScottPlot;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

public class TelemetryPlot(Plot plot) : SufniPlot(plot)
{
    protected Color FrontColor = Color.FromHex("#3288bd");
    protected Color RearColor = Color.FromHex("#66c2a5");

    public virtual void LoadTelemetryData(TelemetryData telemetryData) { }

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