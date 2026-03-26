using System;
using System.Collections.Generic;
using System.Linq;
using ScottPlot;
using ScottPlot.TickGenerators;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

public class CompareFrontRearTravelPlot(Plot plot) : SufniPlot(plot)
{
    public void LoadMultipleSessions(List<(TelemetryData data, Color color, LinePattern pattern, string name)> sessions)
    {
        SetTitle("Front vs rear travel");
        Plot.Layout.Fixed(new PixelPadding(65, 24, 50, 40));
        Plot.Axes.Bottom.Label.Text = "Rear suspension travel (%)";
        Plot.Axes.Left.Label.Text = "Front suspension travel (%)";

        // 1:1 reference line (only once, grey, solid)
        var oneToOne = Plot.Add.Scatter(new double[] { 0.0, 100.0 }, new double[] { 0.0, 100.0 });
        oneToOne.MarkerStyle.IsVisible = false;
        oneToOne.LineStyle.Color = Color.FromHex("#dddddd");
        oneToOne.LineStyle.Width = 1;

        // First pass: compute slopes and draw trend lines
        var slopeData = new List<(Color color, string name, double slope)>();
        foreach (var (data, color, _, name) in sessions)
        {
            if (!data.Front.Present || !data.Rear.Present) continue;

            var count = Math.Min(data.Front.Travel.Length, data.Rear.Travel.Length);
            if (count == 0) continue;

            var rear = data.Rear.Travel
                .Take(count)
                .Select(v => v / data.Linkage.MaxRearTravel * 100.0)
                .ToArray();
            var front = data.Front.Travel
                .Take(count)
                .Select(v => v / data.Linkage.MaxFrontTravel * 100.0)
                .ToArray();

            var denominator = rear.Select(v => v * v).Sum();
            if (denominator > 0)
            {
                var slope = rear.Zip(front, (x, y) => x * y).Sum() / denominator;
                var trend = Plot.Add.Scatter(
                    new double[] { 0.0, 100.0 },
                    new double[] { 0.0, 100.0 * slope });
                trend.MarkerStyle.IsVisible = false;
                trend.LineStyle.Color = color;
                trend.LineStyle.Pattern = LinePattern.Solid;
                trend.LineStyle.Width = 2;

                slopeData.Add((color, name, slope));
            }
        }

        // Second pass: add labels in reverse order so first session is closest to axis (top)
        var labelYOffset = -10 - (slopeData.Count - 1) * 18;
        foreach (var (color, name, slope) in slopeData)
        {
            var colorHex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            AddLabel($"{name}  a={slope:0.00}", 100, 0, -10, labelYOffset, Alignment.LowerRight, colorHex);
            labelYOffset += 18;
        }

        Plot.Axes.SetLimits(0, 100, 0, 100);
        Plot.Axes.Bottom.TickGenerator = new NumericManual(
            [0.0, 10.0, 20.0, 30.0, 40.0, 50.0, 60.0, 70.0, 80.0, 90.0, 100.0],
            ["0", "10", "20", "30", "40", "50", "60", "70", "80", "90", "100"]);
        Plot.Axes.Left.TickGenerator = new NumericManual(
            [0.0, 10.0, 20.0, 30.0, 40.0, 50.0, 60.0, 70.0, 80.0, 90.0, 100.0],
            ["0", "10", "20", "30", "40", "50", "60", "70", "80", "90", "100"]);
    }
}
