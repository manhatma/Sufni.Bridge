using System;
using System.Linq;
using ScottPlot;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

public class FrontRearTravelScatterPlot(Plot plot) : TelemetryPlot(plot)
{
    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);

        if (!telemetryData.Front.Present || !telemetryData.Rear.Present)
        {
            return;
        }

        Plot.Axes.Title.Label.Text = "Front vs rear travel";
        Plot.Layout.Fixed(new PixelPadding(70, 20, 50, 40));
        Plot.Axes.Bottom.Label.Text = "Rear suspension travel (%)";
        Plot.Axes.Left.Label.Text = "Front suspension travel (%)";

        var count = Math.Min(telemetryData.Front.Travel.Length, telemetryData.Rear.Travel.Length);
        if (count == 0)
        {
            return;
        }

        var rear = telemetryData.Rear.Travel
            .Take(count)
            .Select(v => v / telemetryData.Linkage.MaxRearTravel * 100.0)
            .ToArray();
        var front = telemetryData.Front.Travel
            .Take(count)
            .Select(v => v / telemetryData.Linkage.MaxFrontTravel * 100.0)
            .ToArray();

        var scatter = Plot.Add.Scatter(rear, front);
        scatter.LineStyle.IsVisible = false;
        scatter.MarkerStyle.FillColor = Color.FromHex("#d8d8d8").WithOpacity(0.4);
        scatter.MarkerStyle.LineColor = Color.FromHex("#d8d8d8").WithOpacity(0.4);
        scatter.MarkerStyle.Size = 1.5f;

        var oneToOne = Plot.Add.Scatter(new double[] { 0.0, 100.0 }, new double[] { 0.0, 100.0 });
        oneToOne.MarkerStyle.IsVisible = false;
        oneToOne.LineStyle.Color = RearColor;
        oneToOne.LineStyle.Width = 2;

        var denominator = rear.Select(v => v * v).Sum();
        if (denominator > 0)
        {
            var slope = rear.Zip(front, (x, y) => x * y).Sum() / denominator;
            var trend = Plot.Add.Scatter(new double[] { 0.0, 100.0 }, new double[] { 0.0, 100.0 * slope });
            trend.MarkerStyle.IsVisible = false;
            trend.LineStyle.Color = Color.FromHex("#e5df12");
            trend.LineStyle.Width = 2;
            var aLabel = Plot.Add.Text($"a={slope:0.00}", 100, 100 * slope);
            aLabel.LabelFontColor = Color.FromHex("#e5df12");
            aLabel.LabelFontSize = 13;
            aLabel.LabelAlignment = Alignment.LowerRight;
            aLabel.LabelOffsetX = -8;
            aLabel.LabelOffsetY = 0;
        }

        Plot.Axes.SetLimits(0, 100, 0, 100);
    }
}
