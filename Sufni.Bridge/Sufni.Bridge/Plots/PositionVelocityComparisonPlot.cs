using System;
using ScottPlot;
using ScottPlot.TickGenerators;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

public class PositionVelocityComparisonPlot(Plot plot) : TelemetryPlot(plot)
{
    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);

        if (!telemetryData.Front.Present && !telemetryData.Rear.Present)
        {
            return;
        }

        Plot.Axes.Title.Label.Text = "Position vs velocity comparison";
        Plot.Layout.Fixed(new PixelPadding(70, 10, 50, 40));

        Plot.Axes.Bottom.Label.Text = "Travel (mm)";
        Plot.Axes.Left.Label.Text = "Velocity (mm/s)";

        var maxTravel = 0.0;
        var velocityMaxPositive = 0.0;
        var velocityMaxNegative = 0.0;

        void AddSuspension(SuspensionType type)
        {
            var data = telemetryData.CalculatePositionVelocityData(type);
            var travel = type == SuspensionType.Front
                ? telemetryData.Linkage.MaxFrontTravel
                : telemetryData.Linkage.MaxRearTravel;
            var color = type == SuspensionType.Front ? FrontColor : RearColor;

            maxTravel = Math.Max(maxTravel, travel);

            if (data.Travel.Length > 0)
            {
                var scatter = Plot.Add.Scatter(data.Travel, data.Velocity);
                scatter.MarkerStyle.IsVisible = false;
                scatter.LineStyle.IsVisible = true;
                scatter.LineStyle.Width = 1;
                scatter.LineStyle.Color = color.WithOpacity(0.6);

                foreach (var v in data.Velocity)
                {
                    if (v > 0)
                        velocityMaxPositive = Math.Max(velocityMaxPositive, v);
                    else
                        velocityMaxNegative = Math.Max(velocityMaxNegative, Math.Abs(v));
                }
            }
        }

        if (telemetryData.Front.Present)
            AddSuspension(SuspensionType.Front);
        if (telemetryData.Rear.Present)
            AddSuspension(SuspensionType.Rear);

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

        if (telemetryData.Front.Present)
        {
            var frontLegend = Plot.Add.Text("Front", maxTravel, topLimit * 0.95);
            frontLegend.LabelFontColor = FrontColor;
            frontLegend.LabelFontSize = 12;
            frontLegend.LabelAlignment = Alignment.UpperRight;            
            frontLegend.LabelOffsetX = -10; // 1em margin between label right edge and axis

        }
        if (telemetryData.Rear.Present)
        {
            var rearLegend = Plot.Add.Text("Rear", maxTravel, topLimit * 0.87);
            rearLegend.LabelFontColor = RearColor;
            rearLegend.LabelFontSize = 12;
            rearLegend.LabelAlignment = Alignment.UpperRight;            
            rearLegend.LabelOffsetX = -10; // 1em margin between label right edge and axis
   
        }
    }
}
