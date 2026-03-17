using System;
using System.Linq;
using ScottPlot;
using ScottPlot.TickGenerators;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

public class BalancePlot(Plot plot, BalanceType type) : TelemetryPlot(plot)
{
    private void AddStatistics(BalanceData balance, int roundedMaxVelocity)
    {
        var maxVelocity = Math.Max(
            balance.FrontVelocity.Select(Math.Abs).DefaultIfEmpty(0).Max(),
            balance.RearVelocity.Select(Math.Abs).DefaultIfEmpty(0).Max());

        var msd = balance.MeanSignedDeviation / maxVelocity * 100.0;
        var msdString = $"MSD: {msd:+0.00;-#.00} %";

        AddLabel(msdString, 100, 0, -10, -5, Alignment.LowerRight);
    }

    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);

        Plot.Axes.Title.Label.Text = type == BalanceType.Compression ? "Compression balance" : "Rebound balance";
        Plot.Axes.Bottom.Label.Text = "Suspension travel (%)";
        Plot.Axes.Bottom.Label.ForeColor = Color.FromHex("#D0D0D0");
        Plot.Axes.Left.Label.Text = type == BalanceType.Compression ? "Compression velocity (mm/s)" : "Rebound velocity (mm/s)";
        Plot.Axes.Left.Label.ForeColor = Color.FromHex("#D0D0D0");
        Plot.Layout.Fixed(new PixelPadding(70, 10, 40, 50));

        var balance = telemetryData.CalculateBalance(type);

        var maxVelocity = Math.Max(
            balance.FrontVelocity.Select(Math.Abs).DefaultIfEmpty(0).Max(),
            balance.RearVelocity.Select(Math.Abs).DefaultIfEmpty(0).Max());
        var roundedMaxVelocity = (int)Math.Ceiling(maxVelocity / 100.0) * 100;

        if (type == BalanceType.Rebound)
            Plot.Axes.SetLimits(0, 100, 0, -roundedMaxVelocity);  // inverted: 0 at bottom, -max at top
        else
            Plot.Axes.SetLimits(0, 100, 0, roundedMaxVelocity);

        var tickInterval = (int)Math.Ceiling(maxVelocity / 5 / 100.0) * 100;
        Plot.Axes.Left.TickGenerator = new NumericFixedInterval(tickInterval);
        Plot.Axes.Bottom.TickGenerator = new NumericManual(
            [0.0, 10.0, 20.0, 30.0, 40.0, 50.0, 60.0, 70.0, 80.0, 90.0, 100.0],
            ["0", "10", "20", "30", "40", "50", "60", "70", "80", "90", "100"]);

        var dotSize = 2;

        var front = Plot.Add.Scatter(balance.FrontTravel, balance.FrontVelocity);
        front.LineStyle.IsVisible = false;
        front.MarkerStyle.LineColor = FrontColor.WithOpacity();
        front.MarkerStyle.FillColor = FrontColor.WithOpacity();
        front.MarkerStyle.Size = dotSize;

        var frontTrend = Plot.Add.Scatter(balance.FrontTravel, balance.FrontTrend);
        frontTrend.MarkerStyle.IsVisible = false;
        frontTrend.LineStyle.Color = FrontColor;
        frontTrend.LineStyle.Width = 2;

        var rear = Plot.Add.Scatter(balance.RearTravel, balance.RearVelocity);
        rear.LineStyle.IsVisible = false;
        rear.MarkerStyle.LineColor = RearColor.WithOpacity();
        rear.MarkerStyle.FillColor = RearColor.WithOpacity();
        rear.MarkerStyle.Size = dotSize;

        var rearTrend = Plot.Add.Scatter(balance.RearTravel, balance.RearTrend);
        rearTrend.MarkerStyle.IsVisible = false;
        rearTrend.LineStyle.Color = RearColor;
        rearTrend.LineStyle.Width = 2;

        AddStatistics(balance, roundedMaxVelocity);

        // Legend labels
        var legendY1 = type == BalanceType.Compression
            ? roundedMaxVelocity * 0.95
            : -roundedMaxVelocity * 0.95;
        var legendY2 = type == BalanceType.Compression
            ? roundedMaxVelocity * 0.87
            : -roundedMaxVelocity * 0.87;

        var frontLegend = Plot.Add.Text("Front", 100, legendY1);
        frontLegend.LabelFontColor = FrontColor;
        frontLegend.LabelFontSize = 12;
        frontLegend.LabelAlignment = Alignment.UpperRight;

        var rearLegend = Plot.Add.Text("Rear", 100, legendY2);
        rearLegend.LabelFontColor = RearColor;
        rearLegend.LabelFontSize = 12;
        rearLegend.LabelAlignment = Alignment.UpperRight;
    }
}
