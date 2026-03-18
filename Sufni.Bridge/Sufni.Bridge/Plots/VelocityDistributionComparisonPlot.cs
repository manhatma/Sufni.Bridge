using System;
using System.Linq;
using MathNet.Numerics.Statistics;
using ScottPlot;
using ScottPlot.TickGenerators;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

public class VelocityDistributionComparisonPlot(Plot plot) : TelemetryPlot(plot)
{
    // Display range ±2 m/s
    private const double VelocityLimitMs = 2.0;

    private static readonly Color StatColor = Color.FromHex("#FFD700");

    private void AddStatsBox(TelemetryData telemetryData, double topLimit)
    {
        static string N(double v, int w = 7) =>
            v.ToString("F1").PadLeft(w).Replace(' ', '\u00A0');

        string BuildStats(SuspensionType type, TelemetryData td)
        {
            var stats = td.CalculateVelocityStatistics(type);
            var suspension = type == SuspensionType.Front ? td.Front : td.Rear;

            var compVels = suspension.Strokes.Compressions
                .SelectMany(s => suspension.Velocity[s.Start..(s.End + 1)])
                .ToList();
            var rebVels = suspension.Strokes.Rebounds
                .SelectMany(s => suspension.Velocity[s.Start..(s.End + 1)].Select(Math.Abs))
                .ToList();

            var p95Comp = compVels.Count > 0 ? compVels.Percentile(95) : 0.0;
            var p95Reb  = rebVels.Count  > 0 ? rebVels.Percentile(95)  : 0.0;
            var label   = type == SuspensionType.Front ? "Front" : "Rear";

            return
                $"{label}\n" +
                $"Comp\u00A0avg:\u00A0\u00A0{N(stats.AverageCompression)}\u00A0mm/s\n" +
                $"Comp\u00A095th:\u00A0{N(p95Comp)}\u00A0mm/s\n" +
                $"Comp\u00A0max:\u00A0\u00A0{N(stats.MaxCompression)}\u00A0mm/s\n" +
                $"Reb\u00A0avg:\u00A0\u00A0\u00A0{N(Math.Abs(stats.AverageRebound))}\u00A0mm/s\n" +
                $"Reb\u00A095th:\u00A0\u00A0{N(p95Reb)}\u00A0mm/s\n" +
                $"Reb\u00A0max:\u00A0\u00A0\u00A0{N(Math.Abs(stats.MaxRebound))}\u00A0mm/s";
        }

        // Rear stats box: lower-right
        if (telemetryData.Rear.Present)
        {
            var rearBox = Plot.Add.Text(BuildStats(SuspensionType.Rear, telemetryData), VelocityLimitMs, topLimit * 0.60);
            rearBox.LabelFontColor = RearColor;
            rearBox.LabelFontSize = 10;
            rearBox.LabelFontName = "Menlo";
            rearBox.LabelAlignment = Alignment.UpperRight;
            rearBox.LabelOffsetX = -5;
            rearBox.LabelBold = true;
            rearBox.LabelBackgroundColor = Color.FromHex("#15191C").WithAlpha(200);
            rearBox.LabelBorderColor = RearColor.WithAlpha(80);
            rearBox.LabelBorderWidth = 1;
            rearBox.LabelPadding = 5;
        }

        // Front stats box: upper-right
        if (telemetryData.Front.Present)
        {
            var frontBox = Plot.Add.Text(BuildStats(SuspensionType.Front, telemetryData), VelocityLimitMs, topLimit * 0.97);
            frontBox.LabelFontColor = FrontColor;
            frontBox.LabelFontSize = 10;
            frontBox.LabelFontName = "Menlo";
            frontBox.LabelAlignment = Alignment.UpperRight;
            frontBox.LabelOffsetX = -5;
            frontBox.LabelBold = true;
            frontBox.LabelBackgroundColor = Color.FromHex("#15191C").WithAlpha(200);
            frontBox.LabelBorderColor = FrontColor.WithAlpha(80);
            frontBox.LabelBorderWidth = 1;
            frontBox.LabelPadding = 5;
        }
    }

    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);

        if (!telemetryData.Front.Present && !telemetryData.Rear.Present)
        {
            return;
        }

        Plot.Axes.Title.Label.Text = "Velocity distribution comparison";
        // Match Spring tab padding: left=70, right=20, bottom=50, top=40
        Plot.Layout.Fixed(new PixelPadding(70, 20, 50, 40));

        Plot.Axes.Bottom.Label.Text = "Velocity (m/s)";
        Plot.Axes.Left.Label.Text = "Time (%)";

        var maxY = 1.0;

        void AddSuspension(SuspensionType type)
        {
            var data = telemetryData.CalculateVelocityHistogram(type);
            var step = data.Bins[1] - data.Bins[0];
            var color = type == SuspensionType.Front ? FrontColor : RearColor;

            var summedValues = new double[data.Values.Count];
            for (var i = 0; i < data.Values.Count; i++)
                summedValues[i] = data.Values[i].Sum();

            // Velocity on X (m/s), time% on Y — vertical bars
            var bars = summedValues.Select((value, i) => new Bar
            {
                Position = data.Bins[i] / 1000.0,   // mm/s → m/s
                Value = value,
                FillColor = color.WithOpacity(),
                LineColor = color,
                LineWidth = 1f,
                Orientation = Orientation.Vertical,
                Size = step / 1000.0 * 0.9,         // mm/s → m/s
            }).ToList();

            if (bars.Count > 0)
                Plot.Add.Bars(bars);

            if (summedValues.Length > 0)
                maxY = Math.Max(maxY, summedValues.Max());

            // Normal distribution curve: X=velocity(m/s), Y=pdf(time%)
            var normalData = telemetryData.CalculateNormalDistribution(type);
            var normal = Plot.Add.Scatter(
                normalData.Y.Select(v => v / 1000.0).ToArray(),
                normalData.Pdf.ToArray());
            normal.Color = color;
            normal.MarkerStyle.IsVisible = false;
            normal.LineStyle.Width = 3;
            normal.LineStyle.Pattern = LinePattern.Dotted;
        }

        if (telemetryData.Front.Present)
            AddSuspension(SuspensionType.Front);
        if (telemetryData.Rear.Present)
            AddSuspension(SuspensionType.Rear);

        var topLimit = maxY * 1.3;
        Plot.Axes.SetLimits(left: -VelocityLimitMs, right: VelocityLimitMs, bottom: 0, top: topLimit);
        Plot.Axes.Bottom.TickGenerator = new NumericFixedInterval(0.5);

        AddStatsBox(telemetryData, topLimit);
    }
}
