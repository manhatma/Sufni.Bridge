using System;
using System.Linq;
using ScottPlot;
using ScottPlot.TickGenerators;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

public class VelocityDistributionPlot(Plot plot, SuspensionType type) : TelemetryPlot(plot)
{
    private const double VelocityLimit = 2000.0;

    private void AddStatistics(TelemetryData telemetryData)
    {
        var statistics = telemetryData.CalculateVelocityStatistics(type);

        var maxReboundVelString = $"Max reb: {statistics.MaxRebound:0.0} mm/s";
        var avgReboundVelString = $"Avg reb: {statistics.AverageRebound:0.0} mm/s";
        var avgCompVelString = $"Avg cmp: {statistics.AverageCompression:0.0} mm/s";
        var maxCompVelString = $"Max cmp: {statistics.MaxCompression:0.0} mm/s";

        AddLabelWithHorizontalLine(avgReboundVelString, statistics.AverageRebound, LabelLinePosition.Below);
        AddLabelWithHorizontalLine(avgCompVelString, statistics.AverageCompression, LabelLinePosition.Above);
    }

    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);

        SetTitle(type == SuspensionType.Front
            ? "Front velocity distribution"
            : "Rear velocity distribution");
        Plot.Layout.Fixed(new PixelPadding(70, 10, 50, 40));

        Plot.Axes.Bottom.Label.Text = "Velocity (mm/s)";
        Plot.Axes.Left.Label.Text = "Time (%)";

        var data = telemetryData.CalculateVelocityHistogram(type);
        var step = data.Bins[1] - data.Bins[0];
        var color = type == SuspensionType.Front ? FrontColor : RearColor;

        // Sum the stacked travel bins into a single value per velocity bin
        var summedValues = new double[data.Values.Count];
        for (var i = 0; i < data.Values.Count; i++)
        {
            summedValues[i] = data.Values[i].Sum();
        }

        var bars = summedValues.Select((value, i) => new Bar
        {
            Position = data.Bins[i],
            Value = value,
            FillColor = color.WithOpacity(),
            LineColor = color,
            LineWidth = 1f,
            Orientation = Orientation.Vertical,
            Size = step * 0.9,
        }).ToList();

        if (bars.Count > 0)
        {
            Plot.Add.Bars(bars);
        }

        var maxValue = summedValues.Length > 0 ? summedValues.Max() : 1.0;
        Plot.Axes.SetLimits(
            left: -VelocityLimit,
            right: VelocityLimit,
            bottom: 0,
            top: maxValue * 1.3);

        Plot.Axes.Bottom.TickGenerator = new NumericFixedInterval(500);

        // Normal distribution overlay
        var normalData = telemetryData.CalculateNormalDistribution(type);
        var normal = Plot.Add.Scatter(
            normalData.Y.ToArray(),
            normalData.Pdf.ToArray());
        normal.Color = Color.FromHex("#d53e4f");
        normal.MarkerStyle.IsVisible = false;
        normal.LineStyle.Width = 3;
        normal.LineStyle.Pattern = LinePattern.Dotted;

        AddStatistics(telemetryData);
    }
}
