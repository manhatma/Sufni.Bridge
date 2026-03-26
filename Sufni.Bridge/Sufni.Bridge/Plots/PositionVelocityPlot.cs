using System;
using ScottPlot;
using ScottPlot.TickGenerators;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

public class PositionVelocityPlot(Plot plot, SuspensionType type) : TelemetryPlot(plot)
{
    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);

        SetTitle(type == SuspensionType.Front
            ? "Front position vs velocity"
            : "Damper position vs velocity");
        Plot.Layout.Fixed(new PixelPadding(70, 14, 50, 40));

        var useDamperTravel = type == SuspensionType.Rear;
        Plot.Axes.Bottom.Label.Text = useDamperTravel ? "Damper travel (mm)" : "Wheel travel (mm)";
        Plot.Axes.Left.Label.Text = "Velocity (mm/s)";

        var data = useDamperTravel
            ? telemetryData.CalculateDamperPositionVelocityData()
            : telemetryData.CalculatePositionVelocityData(type);
        var maxTravel = useDamperTravel
            ? telemetryData.Linkage.MaxRearStroke!.Value
            : type == SuspensionType.Front
                ? telemetryData.Linkage.MaxFrontTravel
                : telemetryData.Linkage.MaxRearTravel;
        var color = type == SuspensionType.Front ? FrontColor : RearColor;

        var velocityMaxPositive = 0.0;
        var velocityMaxNegative = 0.0;
        if (data.Travel.Length > 0)
        {
            var scatter = Plot.Add.Scatter(data.Travel, data.Velocity);
            scatter.MarkerStyle.IsVisible = false;
            scatter.LineStyle.IsVisible = true;
            scatter.LineStyle.Width = 1;
            scatter.LineStyle.Color = color.WithOpacity(0.9);

            foreach (var v in data.Velocity)
            {
                if (v > 0)
                    velocityMaxPositive = Math.Max(velocityMaxPositive, v);
                else
                    velocityMaxNegative = Math.Max(velocityMaxNegative, Math.Abs(v));
            }
        }

        // Add 10% padding and round up to nearest 500 independently
        var topLimit = Math.Ceiling(velocityMaxPositive * 1.1 / 500.0) * 500.0;
        if (topLimit < 500.0) topLimit = 500.0;
        var bottomLimit = Math.Ceiling(velocityMaxNegative * 1.1 / 500.0) * 500.0;
        if (bottomLimit < 500.0) bottomLimit = 500.0;

        // Zero velocity reference line
        Plot.Add.HorizontalLine(0, 1f, Color.FromHex("#dddddd"), LinePattern.Dotted);

        Plot.Axes.SetLimits(
            left: 0,
            right: maxTravel,
            bottom: -bottomLimit,
            top: topLimit);

        Plot.Axes.Left.TickGenerator = new NumericFixedInterval(500);

        var label = type == SuspensionType.Front ? "Front" : "Rear";
        var legend = Plot.Add.Text(label, maxTravel, topLimit * 0.95);
        legend.LabelFontColor = color;
        legend.LabelFontSize = 12;
        legend.LabelAlignment = Alignment.UpperRight;
        legend.LabelOffsetX = -6;
    }
}
