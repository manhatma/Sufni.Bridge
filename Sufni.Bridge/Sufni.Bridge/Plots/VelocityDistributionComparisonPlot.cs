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

        (double ac, double p5c, double mc, double ar, double p5r, double mr)
            GetStats(SuspensionType type)
        {
            var s = telemetryData.CalculateVelocityStatistics(type);
            var sus = type == SuspensionType.Front ? telemetryData.Front : telemetryData.Rear;
            var cv = sus.Strokes.Compressions
                .SelectMany(x => sus.Velocity[x.Start..(x.End + 1)]).ToList();
            var rv = sus.Strokes.Rebounds
                .SelectMany(x => sus.Velocity[x.Start..(x.End + 1)].Select(Math.Abs)).ToList();
            return (s.AverageCompression,
                    cv.Count > 0 ? cv.Percentile(95) : 0.0,
                    s.MaxCompression,
                    Math.Abs(s.AverageRebound),
                    rv.Count > 0 ? rv.Percentile(95) : 0.0,
                    Math.Abs(s.MaxRebound));
        }

        // Single merged box — "mm/s" in the header saves repeating the unit per line
        string statsText;
        if (telemetryData.Front.Present && telemetryData.Rear.Present)
        {
            var (fac, fp5c, fmc, far, fp5r, fmr) = GetStats(SuspensionType.Front);
            var (rac, rp5c, rmc, rar, rp5r, rmr) = GetStats(SuspensionType.Rear);
            statsText =
                $"mm/s\u00A0\u00A0\u00A0\u00A0\u00A0Front\u00A0\u00A0\u00A0\u00A0\u00A0Rear\n" +
                $"C\u00A0avg:{N(fac)}\u00A0{N(rac)}\n" +
                $"C\u00A095th:{N(fp5c)}\u00A0{N(rp5c)}\n" +
                $"C\u00A0max:{N(fmc)}\u00A0{N(rmc)}\n" +
                $"R\u00A0avg:{N(far)}\u00A0{N(rar)}\n" +
                $"R\u00A095th:{N(fp5r)}\u00A0{N(rp5r)}\n" +
                $"R\u00A0max:{N(fmr)}\u00A0{N(rmr)}";
        }
        else
        {
            var type = telemetryData.Front.Present ? SuspensionType.Front : SuspensionType.Rear;
            var (ac, p5c, mc, ar, p5r, mr) = GetStats(type);
            statsText =
                $"mm/s\n" +
                $"C\u00A0avg:{N(ac)}\n" +
                $"C\u00A095th:{N(p5c)}\n" +
                $"C\u00A0max:{N(mc)}\n" +
                $"R\u00A0avg:{N(ar)}\n" +
                $"R\u00A095th:{N(p5r)}\n" +
                $"R\u00A0max:{N(mr)}";
        }

        var box = Plot.Add.Text(statsText, VelocityLimitMs, topLimit * 0.97);
        box.LabelFontColor = Color.FromHex("#D0D0D0");
        box.LabelFontSize = 9;
        box.LabelFontName = "Menlo";
        box.LabelAlignment = Alignment.UpperRight;
        box.LabelOffsetX = -5;
        box.LabelBold = true;
        box.LabelBackgroundColor = Color.FromHex("#15191C").WithAlpha(210);
        box.LabelBorderColor = Color.FromHex("#808080").WithAlpha(80);
        box.LabelBorderWidth = 1;
        box.LabelPadding = 5;
    }

    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);

        if (!telemetryData.Front.Present && !telemetryData.Rear.Present)
        {
            return;
        }

        SetTitle("Velocity distribution comparison");
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
