using System.Collections.Generic;
using System.Linq;
using ScottPlot;
using ScottPlot.TickGenerators;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

public class LowSpeedVelocityHistogramPlot(Plot plot, SuspensionType type, double highSpeedThreshold = 200)
    : TelemetryPlot(plot)
{
    private readonly double velocityLimit = highSpeedThreshold + 50;

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

    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);

        SetTitle(type == SuspensionType.Front
            ? "Front low-speed velocity"
            : "Rear low-speed velocity");

        Plot.Layout.Fixed(new PixelPadding(50, 24, 50, 40));

        Plot.Axes.Bottom.Label.Text = "Velocity (mm/s)";
        Plot.Axes.Left.Label.Text = "Time (%)";

        var data = telemetryData.CalculateLowSpeedVelocityHistogram(type, highSpeedThreshold);
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
                    Position = data.Bins[i] + step / 2.0,
                    ValueBase = nextBarBase,
                    Value = nextBarBase + data.Values[i][j],
                    FillColor = palette[j].WithOpacity(0.8),
                    LineColor = Colors.Black,
                    LineWidth = 0.5f,
                    Orientation = Orientation.Vertical,
                    Size = step * 0.95,
                });

                nextBarBase += data.Values[i][j];
            }
        }

        var yRangeTop = System.Math.Max(1.0, maxY) * 1.3;

        Plot.Axes.SetLimits(left: -velocityLimit, right: velocityLimit, bottom: 0, top: yRangeTop);
        Plot.Axes.Bottom.TickGenerator = new NumericFixedInterval(50);

        Plot.Add.VerticalLine(0, 1f, Color.FromHex("#dddddd"), LinePattern.Dotted);

        // Normal distribution overlay — x range matches plot display range (±velocityLimit)
        var normalData = telemetryData.CalculateLowSpeedNormalDistribution(type, highSpeedThreshold);
        var normal = Plot.Add.Scatter(
            normalData.Y.ToArray(),
            normalData.Pdf.ToArray());
        normal.Color = Color.FromHex("#d53e4f");
        normal.MarkerStyle.IsVisible = false;
        normal.LineStyle.Width = 3;
        normal.LineStyle.Pattern = LinePattern.Dotted;
    }
}
