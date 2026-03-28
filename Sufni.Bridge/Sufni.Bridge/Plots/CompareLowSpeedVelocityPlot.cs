using System;
using System.Collections.Generic;
using System.Linq;
using ScottPlot;
using ScottPlot.TickGenerators;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

public class CompareLowSpeedVelocityPlot(Plot plot, SuspensionType type, double highSpeedThreshold = 200)
    : SufniPlot(plot)
{
    private const double HistogramRangeMultiplier = 1.3;
    private readonly double _velocityLimit = highSpeedThreshold + 50;

    public void LoadMultipleSessions(List<(TelemetryData data, Color color, LinePattern pattern, string name)> sessions)
    {
        SetTitle(type == SuspensionType.Front
            ? "Front low-speed velocity"
            : "Rear low-speed velocity");
        Plot.Layout.Fixed(new PixelPadding(50, 24, 50, 40));
        Plot.Axes.Bottom.Label.Text = "Velocity (mm/s)";
        Plot.Axes.Left.Label.Text = "Time (%)";

        double globalMaxTime = 0;

        foreach (var (data, color, pattern, name) in sessions)
        {
            var suspension = type == SuspensionType.Front ? data.Front : data.Rear;
            if (!suspension.Present) continue;

            var histData = data.CalculateLowSpeedVelocityHistogram(type, highSpeedThreshold);
            if (histData.Bins.Count < 2) continue;

            var step = histData.Bins[1] - histData.Bins[0];

            // Sum travel zones per velocity bin and build polygon points
            var pxs = new List<double>();
            var pys = new List<double>();

            var firstBinAdded = false;
            for (var i = 0; i < histData.Values.Count; i++)
            {
                var midpoint = histData.Bins[i] + step / 2.0;
                if (midpoint <= -(_velocityLimit) || midpoint >= _velocityLimit) continue;

                var total = 0.0;
                for (var j = 0; j < histData.Values[i].Length; j++)
                    total += histData.Values[i][j];

                if (total > globalMaxTime) globalMaxTime = total;

                var left = histData.Bins[i];
                var right = histData.Bins[i] + step;

                if (!firstBinAdded)
                {
                    pxs.Add(left);
                    pys.Add(0);
                    firstBinAdded = true;
                }

                pxs.Add(left);
                pys.Add(total);
                pxs.Add(right);
                pys.Add(total);
            }

            if (pxs.Count > 0)
            {
                pxs.Add(pxs[^1]);
                pys.Add(0);

                var polygon = Plot.Add.Polygon(pxs.ToArray(), pys.ToArray());
                polygon.FillStyle.Color = color.WithOpacity(0.15f);
                polygon.LineStyle.Color = color;
                polygon.LineStyle.Width = 2;
                polygon.LineStyle.Pattern = LinePattern.Solid;
            }

            // Normal distribution overlay
            var normalData = data.CalculateLowSpeedNormalDistribution(type, highSpeedThreshold);
            var normal = Plot.Add.Scatter(
                normalData.Y.ToArray(),
                normalData.Pdf.ToArray());
            normal.Color = color;
            normal.MarkerStyle.IsVisible = false;
            normal.LineStyle.Width = 3;
            normal.LineStyle.Pattern = LinePattern.Dotted;
        }

        Plot.Add.VerticalLine(0, 1f, Color.FromHex("#dddddd"), LinePattern.Dotted);

        var yRangeTop = Math.Max(1.0, globalMaxTime) * HistogramRangeMultiplier;
        Plot.Axes.SetLimits(left: -_velocityLimit, right: _velocityLimit, bottom: 0, top: yRangeTop);
        Plot.Axes.Bottom.TickGenerator = new NumericFixedInterval(50);

        // Legend
        var legendY = yRangeTop * 0.95;
        var legendStep = yRangeTop * 0.08;
        for (var i = 0; i < sessions.Count; i++)
        {
            var (_, color, _, name) = sessions[i];
            var label = Plot.Add.Text(name, _velocityLimit, legendY - i * legendStep);
            label.LabelFontColor = color;
            label.LabelFontSize = 12;
            label.LabelAlignment = Alignment.UpperRight;
            label.LabelOffsetX = -6;
        }
    }
}
