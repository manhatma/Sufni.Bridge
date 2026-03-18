using System.Collections.Generic;
using System.Linq;
using MathNet.Numerics.Statistics;
using ScottPlot;
using ScottPlot.TickGenerators;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

public class VelocityHistogramPlot(Plot plot, SuspensionType type) : TelemetryPlot(plot)
{
    // Display range ±2 m/s — matches VelocityDistributionComparisonPlot
    private const double VelocityLimitMs = 2.0;

    private readonly List<Color> palette =
    [
        Color.FromHex("#3288bd"),
        Color.FromHex("#66c2a5"),
        Color.FromHex("#abdda4"),
        Color.FromHex("#e6f598"),
        Color.FromHex("#ffffbf"),
        Color.FromHex("#fee08b"),
        Color.FromHex("#fdae61"),
        Color.FromHex("#f46d43"),
        Color.FromHex("#d53e4f"),
        Color.FromHex("#9e0142"),
    ];

    private static readonly Color StatColor = Color.FromHex("#FFD700");

    /// <summary>
    /// Stats box with avg/95th/max in mm/s — placed in the top padding area just below the title.
    /// </summary>
    private void AddStatsBox(TelemetryData telemetryData, double yRangeTop)
    {
        var stats = telemetryData.CalculateVelocityStatistics(type);
        var suspension = type == SuspensionType.Front ? telemetryData.Front : telemetryData.Rear;

        var compVels = suspension.Strokes.Compressions
            .SelectMany(s => suspension.Velocity[s.Start..(s.End + 1)])
            .ToList();
        var rebVels = suspension.Strokes.Rebounds
            .SelectMany(s => suspension.Velocity[s.Start..(s.End + 1)].Select(System.Math.Abs))
            .ToList();

        var p95Comp = compVels.Count > 0 ? compVels.Percentile(95) : 0.0;
        var p95Reb  = rebVels.Count  > 0 ? rebVels.Percentile(95)  : 0.0;

        // Non-breaking spaces keep columns aligned in ScottPlot SVG rendering
        static string N(double v, int w = 7) =>
            v.ToString("F1").PadLeft(w).Replace(' ', '\u00A0');

        var statsText =
            $"Comp\u00A0avg:\u00A0\u00A0{N(stats.AverageCompression)}\u00A0mm/s\n" +
            $"Comp\u00A095th:\u00A0{N(p95Comp)}\u00A0mm/s\n" +
            $"Comp\u00A0max:\u00A0\u00A0{N(stats.MaxCompression)}\u00A0mm/s\n" +
            $"Reb\u00A0avg:\u00A0\u00A0\u00A0{N(System.Math.Abs(stats.AverageRebound))}\u00A0mm/s\n" +
            $"Reb\u00A095th:\u00A0\u00A0{N(p95Reb)}\u00A0mm/s\n" +
            $"Reb\u00A0max:\u00A0\u00A0\u00A0{N(System.Math.Abs(stats.MaxRebound))}\u00A0mm/s";

        // LowerLeft alignment at y=yRangeTop → text extends upward into the 100px top padding
        var box = Plot.Add.Text(statsText, -VelocityLimitMs, yRangeTop);
        box.LabelFontColor = StatColor;
        box.LabelFontSize = 10;
        box.LabelFontName = "Menlo";
        box.LabelAlignment = Alignment.LowerLeft;
        box.LabelOffsetX = 5;
        box.LabelOffsetY = -4;
        box.LabelBold = true;
        box.LabelBackgroundColor = Color.FromHex("#15191C").WithAlpha(220);
        box.LabelBorderColor = StatColor.WithAlpha(80);
        box.LabelBorderWidth = 1;
        box.LabelPadding = 5;
    }

    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);

        SetTitle(type == SuspensionType.Front
            ? "Front velocity"
            : "Rear velocity");

        // Left=70 (Y-axis label), Right=20, Bottom=50, Top=100 (stats zone below title)
        Plot.Layout.Fixed(new PixelPadding(70, 20, 50, 100));

        Plot.Axes.Bottom.Label.Text = "Velocity (m/s)";
        Plot.Axes.Left.Label.Text = "Time (%)";

        var data = telemetryData.CalculateVelocityHistogram(type);
        var step = data.Bins[1] - data.Bins[0];
        var maxY = 0.0;

        for (var i = 0; i < data.Values.Count; ++i)
        {
            double nextBarBase = 0;
            double colTotal = 0;
            for (var j = 0; j < TelemetryData.TravelBinsForVelocityHistogram; j++)
                colTotal += data.Values[i][j];
            if (colTotal > maxY) maxY = colTotal;

            for (var j = 0; j < TelemetryData.TravelBinsForVelocityHistogram; j++)
            {
                if (data.Values[i][j] == 0) continue;

                Plot.Add.Bar(new Bar
                {
                    Position = data.Bins[i] / 1000.0,   // mm/s → m/s on X axis
                    ValueBase = nextBarBase,
                    Value = nextBarBase + data.Values[i][j],
                    FillColor = palette[j].WithOpacity(0.8),
                    LineColor = Colors.Black,
                    LineWidth = 0.5f,
                    Orientation = Orientation.Vertical,  // velocity on X, time% on Y
                    Size = step / 1000.0 * 0.95,         // mm/s → m/s
                });

                nextBarBase += data.Values[i][j];
            }
        }

        var yRangeTop = System.Math.Max(1.0, maxY) * 1.3;

        // X: ±2 m/s with 0.5 m/s ticks — same scale as VelocityDistributionComparison
        Plot.Axes.SetLimits(left: -VelocityLimitMs, right: VelocityLimitMs, bottom: 0, top: yRangeTop);
        Plot.Axes.Bottom.TickGenerator = new NumericFixedInterval(0.5);

        // Normal distribution: X=velocity (m/s), Y=pdf (time%)
        var normalData = telemetryData.CalculateNormalDistribution(type);
        var normal = Plot.Add.Scatter(
            normalData.Y.Select(v => v / 1000.0).ToArray(),
            normalData.Pdf.ToArray());
        normal.Color = Color.FromHex("#d53e4f");
        normal.MarkerStyle.IsVisible = false;
        normal.LineStyle.Width = 3;
        normal.LineStyle.Pattern = LinePattern.Dotted;

        AddStatsBox(telemetryData, yRangeTop);
    }
}
