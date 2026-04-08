using System;
using System.Linq;
using ScottPlot;
using ScottPlot.TickGenerators;
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

        SetTitle("Front vs rear travel");
        Plot.Layout.Fixed(new PixelPadding(65 - (int)Plot.Axes.Left.TickLabelStyle.FontSize, 24, 50, 40));
        Plot.Axes.Bottom.Label.Text = "Rear suspension travel (%)";
        Plot.Axes.Left.Label.Text = "Front suspension travel (%)";

        var count = Math.Min(telemetryData.Front.Travel.Length, telemetryData.Rear.Travel.Length);
        if (count == 0)
        {
            return;
        }

        const int maxScatterPoints = 50_000;
        var stride = count > maxScatterPoints ? count / maxScatterPoints : 1;
        var sampledCount = (count + stride - 1) / stride;

        var rear = new double[sampledCount];
        var front = new double[sampledCount];
        for (int i = 0, j = 0; i < count && j < sampledCount; i += stride, j++)
        {
            rear[j] = telemetryData.Rear.Travel[i] / telemetryData.Linkage.MaxRearTravel * 100.0;
            front[j] = telemetryData.Front.Travel[i] / telemetryData.Linkage.MaxFrontTravel * 100.0;
        }

        var scatter = Plot.Add.Scatter(rear, front);
        scatter.LineStyle.IsVisible = false;
        scatter.MarkerStyle.FillColor = Color.FromHex("#d8d8d8").WithOpacity(0.4);
        scatter.MarkerStyle.LineColor = Color.FromHex("#d8d8d8").WithOpacity(0.4);
        scatter.MarkerStyle.Size = 1.5f;

        var oneToOne = Plot.Add.Scatter(new double[] { 0.0, 100.0 }, new double[] { 0.0, 100.0 });
        oneToOne.MarkerStyle.IsVisible = false;
        oneToOne.LineStyle.Color = RearColor;
        oneToOne.LineStyle.Width = 2;
        oneToOne.LineStyle.Pattern = LinePattern.Dashed;

        var denominator = rear.Select(v => v * v).Sum();
        if (denominator > 0)
        {
            var slope = rear.Zip(front, (x, y) => x * y).Sum() / denominator;
            var trend = Plot.Add.Scatter(new double[] { 0.0, 100.0 }, new double[] { 0.0, 100.0 * slope });
            trend.MarkerStyle.IsVisible = false;
            trend.LineStyle.Color = Color.FromHex("#e5df12");
            trend.LineStyle.Width = 2;
            AddLabel($"a={slope:0.00}", 100, 0, -10, -10, Alignment.LowerRight, "#e5df12");
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
