using System;
using System.Collections.Generic;
using System.Linq;
using ScottPlot;
using ScottPlot.TickGenerators;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

public class CompareDamperVelocityHistogramPlot(Plot plot) : SufniPlot(plot)
{
    private const double HistogramRangeMultiplier = 1.3;

    public void LoadMultipleSessions(List<(TelemetryData data, Color color, LinePattern pattern, string name)> sessions)
    {
        SetTitle("Rear shaft velocity (damper domain)");
        Plot.Layout.Fixed(new PixelPadding(50, 24, 50, 40));
        Plot.Axes.Bottom.Label.Text = "Shaft velocity (mm/s)";
        Plot.Axes.Left.Label.Text = "Time (%)";

        double globalMaxTime = 0;

        // Cache each session's histogram (and step) from pass 1 so pass 2 can draw without recomputing.
        var cached = new List<(Color color, StackedHistogramData histData, double step)>();
        double sharedLimit = 0;

        // Pass 1: compute histograms, collapse travel zones, track the global max time and the
        // shared symmetric X limit (max across sessions of each session's 99%-mass limit).
        foreach (var (data, color, _, _) in sessions)
        {
            if (!data.Rear.Present) continue;

            var histData = data.CalculateDamperVelocityHistogram();
            if (histData.Bins.Count < 2) continue;

            var step = histData.Bins[1] - histData.Bins[0];
            cached.Add((color, histData, step));

            var sessionLimit = SymmetricMassLimit(histData, step, 0.99);
            if (sessionLimit > sharedLimit) sharedLimit = sessionLimit;
        }

        // Pass 2: draw each session's collapsed step-polygon (mm/s, no /1000 conversion) and Gaussian.
        foreach (var (color, histData, step) in cached)
        {
            var pxs = new List<double>();
            var pys = new List<double>();
            var firstBinAdded = false;

            for (var i = 0; i < histData.Values.Count; i++)
            {
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
        }

        // Normal-distribution overlay per session (Y is already mm/s — no /1000 conversion).
        foreach (var (data, color, _, _) in sessions)
        {
            if (!data.Rear.Present) continue;

            var normalData = data.CalculateDamperNormalDistribution();
            var normal = Plot.Add.Scatter(
                normalData.Y.ToArray(),
                normalData.Pdf.ToArray());
            normal.Color = color;
            normal.MarkerStyle.IsVisible = false;
            normal.LineStyle.Width = 2;
            normal.LineStyle.Pattern = LinePattern.Dotted;
        }

        Plot.Add.VerticalLine(0, 1f, Color.FromHex("#dddddd"), LinePattern.Dotted);

        var limit = sharedLimit;
        if (limit <= 0) limit = cached.Count > 0 ? cached[0].step : 1.0;

        var yRangeTop = Math.Max(1.0, globalMaxTime) * HistogramRangeMultiplier;
        Plot.Axes.SetLimits(left: -limit, right: limit, bottom: 0, top: yRangeTop);
        Plot.Axes.Bottom.TickGenerator = new NumericFixedInterval(NiceTickInterval(limit));

        // Legend
        var legendY = yRangeTop * 0.95;
        var legendStep = yRangeTop * 0.08;
        for (var i = 0; i < sessions.Count; i++)
        {
            var (_, color, _, name) = sessions[i];
            var label = Plot.Add.Text(name, limit, legendY - i * legendStep);
            label.LabelFontColor = color;
            label.LabelFontSize = 12;
            label.LabelAlignment = Alignment.UpperRight;
            label.LabelOffsetX = -6;
        }
    }

    // Picks a round tick spacing (~4 divisions per side) so mm/s labels stay readable and
    // don't overlap, regardless of the bike's shaft-velocity range.
    private static double NiceTickInterval(double limit)
    {
        var raw = limit / 4.0;
        double[] steps = [50d, 100d, 200d, 250d, 500d, 1000d, 2000d, 5000d];
        foreach (var s in steps)
            if (s >= raw) return s;
        return 5000d;
    }

    // Smallest symmetric |velocity| whose [-limit, +limit] window covers `coverage` of the total
    // histogram mass — keeps rare high-speed outliers from blowing the axis out and squashing detail.
    private static double SymmetricMassLimit(StackedHistogramData data, double step, double coverage)
    {
        var total = data.Values.Sum(v => v.Sum());
        if (total <= 0)
            return Math.Max(Math.Abs(data.Bins.First()), Math.Abs(data.Bins.Last()));

        var allowedTrim = total * (1.0 - coverage);
        var ordered = new List<(double AbsMid, double Mass)>(data.Values.Count);
        for (var i = 0; i < data.Values.Count; i++)
            ordered.Add((Math.Abs(data.Bins[i] + step / 2.0), data.Values[i].Sum()));
        ordered.Sort((a, b) => b.AbsMid.CompareTo(a.AbsMid));

        var trimmed = 0.0;
        var limit = ordered.Count > 0 ? ordered[0].AbsMid : 0.0;
        foreach (var (absMid, mass) in ordered)
        {
            if (trimmed + mass > allowedTrim)
            {
                limit = absMid;
                break;
            }
            trimmed += mass;
        }
        return limit;
    }
}
