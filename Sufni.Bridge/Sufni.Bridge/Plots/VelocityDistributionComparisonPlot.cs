using System;
using System.Linq;
using ScottPlot;
using ScottPlot.TickGenerators;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

public class VelocityDistributionComparisonPlot(Plot plot) : TelemetryPlot(plot)
{
    private const double VelocityLimit = 2000.0;

    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);

        if (!telemetryData.Front.Present && !telemetryData.Rear.Present)
        {
            return;
        }

        Plot.Axes.Title.Label.Text = "Velocity distribution comparison";
        Plot.Layout.Fixed(new PixelPadding(70, 10, 50, 40));

        Plot.Axes.Left.Label.Text = "Velocity (mm/s)";
        Plot.Axes.Left.Label.FontSize = 12;
        Plot.Axes.Left.TickLabelStyle.FontSize = 10;
        Plot.Axes.Bottom.Label.Text = "Time (%)";
        Plot.Axes.Bottom.Label.FontSize = 12;
        Plot.Axes.Bottom.TickLabelStyle.FontSize = 10;

        var maxX = 1.0;

        void AddSuspension(SuspensionType type)
        {
            var data = telemetryData.CalculateVelocityHistogram(type);
            var step = data.Bins[1] - data.Bins[0];
            var color = type == SuspensionType.Front ? FrontColor : RearColor;

            var summedValues = new double[data.Values.Count];
            for (var i = 0; i < data.Values.Count; i++)
                summedValues[i] = data.Values[i].Sum();

            var bars = summedValues.Select((value, i) => new Bar
            {
                Position = data.Bins[i],
                Value = value,
                FillColor = color.WithOpacity(),
                LineColor = color,
                LineWidth = 1f,
                Orientation = Orientation.Horizontal,
                Size = step * 0.9,
            }).ToList();

            if (bars.Count > 0)
                Plot.Add.Bars(bars);

            if (summedValues.Length > 0)
                maxX = Math.Max(maxX, summedValues.Max());

            var normalData = telemetryData.CalculateNormalDistribution(type);
            var normal = Plot.Add.Scatter(
                normalData.Pdf.ToArray(),
                normalData.Y.ToArray());
            normal.Color = color;
            normal.MarkerStyle.IsVisible = false;
            normal.LineStyle.Width = 3;
            normal.LineStyle.Pattern = LinePattern.Dotted;
        }

        if (telemetryData.Front.Present)
            AddSuspension(SuspensionType.Front);
        if (telemetryData.Rear.Present)
            AddSuspension(SuspensionType.Rear);

        Plot.Axes.SetLimits(
            left: 0.1,
            bottom: VelocityLimit,
            top: -VelocityLimit);
        Plot.Axes.Left.TickGenerator = new NumericFixedInterval(500);

        var limitsRight = maxX * 1.3;
        Plot.Axes.SetLimits(right: limitsRight);

        if (telemetryData.Front.Present)
        {
            var frontLegend = Plot.Add.Text("Front", limitsRight, -VelocityLimit * 0.95);
            frontLegend.LabelFontColor = FrontColor;
            frontLegend.LabelFontSize = 11;
            frontLegend.LabelAlignment = Alignment.UpperRight;
            frontLegend.LabelOffsetX = -10; // 1em margin between label right edge and axis
        }
        if (telemetryData.Rear.Present)
        {
            var rearLegend = Plot.Add.Text("Rear", limitsRight, -VelocityLimit * 0.87);
            rearLegend.LabelFontColor = RearColor;
            rearLegend.LabelFontSize = 11;
            rearLegend.LabelAlignment = Alignment.UpperRight;
            rearLegend.LabelOffsetX = -10; // 1em margin between label right edge and axis
        }
    }
}
