using System;
using System.Collections.Generic;
using System.Linq;
using ScottPlot;
using ScottPlot.TickGenerators;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

public class CompareVelocityHistogramPlot(Plot plot, SuspensionType type) : SufniPlot(plot)
{
    private const double VelocityLimitMs = 2.0;
    private const double HistogramRangeMultiplier = 1.3;

    public void LoadMultipleSessions(List<(TelemetryData data, Color color, LinePattern pattern, string name)> sessions)
    {
        SetTitle(type == SuspensionType.Front
            ? "Front wheel velocity"
            : "Rear wheel velocity");
        Plot.Layout.Fixed(new PixelPadding(50, 24, 50, 40));
        Plot.Axes.Bottom.Label.Text = "Velocity (m/s)";
        Plot.Axes.Left.Label.Text = "Time (%)";

        double globalMaxTime = 0;

        foreach (var (data, color, _, _) in sessions)
        {
            var suspension = type == SuspensionType.Front ? data.Front : data.Rear;
            if (!suspension.Present) continue;

            var histData = data.CalculateVelocityHistogram(type);
            if (histData.Bins.Count < 2) continue;

            var step = histData.Bins[1] - histData.Bins[0];

            var pxs = new List<double>();
            var pys = new List<double>();
            var firstBinAdded = false;

            for (var i = 0; i < histData.Values.Count; i++)
            {
                var total = 0.0;
                for (var j = 0; j < histData.Values[i].Length; j++)
                    total += histData.Values[i][j];

                if (total > globalMaxTime) globalMaxTime = total;

                var leftMs = histData.Bins[i] / 1000.0;
                var rightMs = (histData.Bins[i] + step) / 1000.0;

                if (!firstBinAdded)
                {
                    pxs.Add(leftMs);
                    pys.Add(0);
                    firstBinAdded = true;
                }

                pxs.Add(leftMs);
                pys.Add(total);
                pxs.Add(rightMs);
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

            // Normal-distribution overlay matches single-session VelocityHistogramPlot
            var normalData = data.CalculateNormalDistribution(type);
            var normal = Plot.Add.Scatter(
                normalData.Y.Select(v => v / 1000.0).ToArray(),
                normalData.Pdf.ToArray());
            normal.Color = color;
            normal.MarkerStyle.IsVisible = false;
            normal.LineStyle.Width = 2;
            normal.LineStyle.Pattern = LinePattern.Dotted;
        }

        Plot.Add.VerticalLine(0, 1f, Color.FromHex("#dddddd"), LinePattern.Dotted);

        var yRangeTop = Math.Max(1.0, globalMaxTime) * HistogramRangeMultiplier;
        Plot.Axes.SetLimits(left: -VelocityLimitMs, right: VelocityLimitMs, bottom: 0, top: yRangeTop);
        Plot.Axes.Bottom.TickGenerator = new NumericFixedInterval(0.5);

        // Legend
        var legendY = yRangeTop * 0.95;
        var legendStep = yRangeTop * 0.08;
        for (var i = 0; i < sessions.Count; i++)
        {
            var (_, color, _, name) = sessions[i];
            var label = Plot.Add.Text(name, VelocityLimitMs, legendY - i * legendStep);
            label.LabelFontColor = color;
            label.LabelFontSize = 12;
            label.LabelAlignment = Alignment.UpperRight;
            label.LabelOffsetX = -6;
        }
    }
}
