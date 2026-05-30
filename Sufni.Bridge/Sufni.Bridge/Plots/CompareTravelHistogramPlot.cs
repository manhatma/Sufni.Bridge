using System;
using System.Collections.Generic;
using System.Linq;
using ScottPlot;
using ScottPlot.TickGenerators;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

public class CompareTravelHistogramPlot(Plot plot, SuspensionType type) : SufniPlot(plot)
{
    private const double HistogramRangeMultiplier = 1.3;

    public void LoadMultipleSessions(List<(TelemetryData data, Color color, LinePattern pattern, string name)> sessions)
    {
        SetTitle(type == SuspensionType.Front
            ? "Front travel histogram"
            : "Rear travel histogram");
        Plot.Layout.Fixed(new PixelPadding(50, 24, 50, 40));
        Plot.Axes.Bottom.Label.Text = "Travel (mm)";
        Plot.Axes.Left.Label.Text = "Time (%)";

        double globalMaxTime = 0;
        double globalMaxTravel = 0;

        foreach (var (data, color, pattern, name) in sessions)
        {
            var suspension = type == SuspensionType.Front ? data.Front : data.Rear;
            if (!suspension.Present) continue;

            var histData = data.CalculateDetailedTravelHistogram(type, 2.5);
            if (histData.TravelMidsMm.Count == 0) continue;

            var maxTime = histData.TimePercentage.Max();
            if (maxTime > globalMaxTime) globalMaxTime = maxTime;
            if (histData.MaxTravelMm > globalMaxTravel) globalMaxTravel = histData.MaxTravelMm;

            var firstLeft = histData.TravelMidsMm.Count > 0 && histData.BarWidthsMm.Count > 0
                ? histData.TravelMidsMm[0] - histData.BarWidthsMm[0] / 2.0
                : 0.0;
            var pxs = new System.Collections.Generic.List<double> { firstLeft };
            var pys = new System.Collections.Generic.List<double> { 0 };
            for (var i = 0; i < histData.TravelMidsMm.Count; i++)
            {
                var hw = (histData.BarWidthsMm.Count > i ? histData.BarWidthsMm[i] : histData.BarWidthsMm[0]) / 2.0;
                pxs.Add(histData.TravelMidsMm[i] - hw);
                pys.Add(histData.TimePercentage[i]);
                pxs.Add(histData.TravelMidsMm[i] + hw);
                pys.Add(histData.TimePercentage[i]);
            }
            pxs.Add(histData.MaxTravelMm);
            pys.Add(0);
            var polygon = Plot.Add.Polygon(pxs.ToArray(), pys.ToArray());
            polygon.FillStyle.Color = color.WithOpacity(0.15f);
            polygon.LineStyle.Color = color;
            polygon.LineStyle.Width = 2;
            polygon.LineStyle.Pattern = LinePattern.Solid;
        }

        var yRangeTop = Math.Max(1.0, globalMaxTime) * HistogramRangeMultiplier;
        Plot.Axes.SetLimits(left: 0, right: globalMaxTravel, bottom: 0, top: yRangeTop);

        // Stat lines: avg, 95th, max per session
        var labelYBase = yRangeTop * 0.92;
        var labelYStep = yRangeTop * 0.08;
        for (var i = 0; i < sessions.Count; i++)
        {
            var (data, color, _, _) = sessions[i];
            var suspension = type == SuspensionType.Front ? data.Front : data.Rear;
            if (!suspension.Present) continue;

            var stats = data.CalculateDetailedTravelStatistics(type);
            Plot.Add.VerticalLine(stats.Average, 2f, color, LinePattern.Dashed);
            Plot.Add.VerticalLine(stats.P95, 2f, color, LinePattern.Dashed);
            Plot.Add.VerticalLine(stats.Max, 2f, color, LinePattern.Dashed);

            var labelY = labelYBase - i * labelYStep;
            void AddLabel(string text, double x)
            {
                var t = Plot.Add.Text(text, x, labelY);
                t.LabelFontColor = color;
                t.LabelFontSize = 11;
                t.LabelAlignment = Alignment.LowerCenter;
                t.LabelRotation = -90;
                t.LabelOffsetX = 0;
                t.LabelOffsetY = 4;
            }

            AddLabel("avg", stats.Average);
            AddLabel("95th", stats.P95);
            AddLabel("max", stats.Max);
        }

        // Legend: session names in their colors, centered on right edge
        var legendCenter = yRangeTop * 0.50;
        var legendStep = yRangeTop * 0.08;
        var legendTop = legendCenter + (sessions.Count - 1) / 2.0 * legendStep;
        for (var i = 0; i < sessions.Count; i++)
        {
            var (_, color, _, name) = sessions[i];
            var label = Plot.Add.Text(name, globalMaxTravel, legendTop - i * legendStep);
            label.LabelFontColor = color;
            label.LabelFontSize = 12;
            label.LabelAlignment = Alignment.UpperRight;
            label.LabelOffsetX = -6;
        }
    }
}
