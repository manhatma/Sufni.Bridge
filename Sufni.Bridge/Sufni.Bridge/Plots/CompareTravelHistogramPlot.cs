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

        // Legend: session names in their colors
        var legendY = yRangeTop * 0.95;
        var legendStep = yRangeTop * 0.08;
        for (var i = 0; i < sessions.Count; i++)
        {
            var (_, color, _, name) = sessions[i];
            var label = Plot.Add.Text(name, globalMaxTravel, legendY - i * legendStep);
            label.LabelFontColor = color;
            label.LabelFontSize = 12;
            label.LabelAlignment = Alignment.UpperRight;
            label.LabelOffsetX = -6;
        }
    }
}
