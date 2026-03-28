using System;
using System.Linq;
using ScottPlot;
using ScottPlot.TickGenerators;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

public class CombinedBalancePlot(Plot plot) : TelemetryPlot(plot)
{
    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);

        SetTitle("Balance");
        Plot.Axes.Bottom.Label.Text = "Suspension travel (%)";
        Plot.Axes.Bottom.Label.ForeColor = Color.FromHex("#D0D0D0");
        Plot.Axes.Left.Label.Text = "Velocity (mm/s)";
        Plot.Axes.Left.Label.ForeColor = Color.FromHex("#D0D0D0");
        Plot.Layout.Fixed(new PixelPadding(76, 14, 50, 40));

        var compBalance = telemetryData.CalculateBalance(BalanceType.Compression);
        var rebBalance = telemetryData.CalculateBalance(BalanceType.Rebound);

        // Determine axis limits from both compression and rebound
        var maxCompVelocity = Math.Max(
            compBalance.FrontTrend.Select(Math.Abs).DefaultIfEmpty(0).Max(),
            compBalance.RearTrend.Select(Math.Abs).DefaultIfEmpty(0).Max());
        var maxRebVelocity = Math.Max(
            rebBalance.FrontTrend.Select(Math.Abs).DefaultIfEmpty(0).Max(),
            rebBalance.RearTrend.Select(Math.Abs).DefaultIfEmpty(0).Max());

        var roundedMaxComp = (int)Math.Ceiling(maxCompVelocity / 100.0) * 100;
        var roundedMaxReb = (int)Math.Ceiling(maxRebVelocity / 100.0) * 100;

        // Q1: compression (positive), Q4: rebound (negative, natural sign)
        Plot.Axes.SetLimits(0, 100, -roundedMaxReb, roundedMaxComp);

        var tickInterval = (int)Math.Ceiling(Math.Max(maxCompVelocity, maxRebVelocity) / 5 / 100.0) * 100;
        Plot.Axes.Left.TickGenerator = new NumericFixedInterval(tickInterval);
        Plot.Axes.Bottom.TickGenerator = new NumericManual(
            [0.0, 10.0, 20.0, 30.0, 40.0, 50.0, 60.0, 70.0, 80.0, 90.0, 100.0],
            ["0", "10", "20", "30", "40", "50", "60", "70", "80", "90", "100"]);

        // Zero velocity reference line
        Plot.Add.HorizontalLine(0, 1f, Color.FromHex("#dddddd"), LinePattern.Dotted);

        // Compression trend lines (Q1)
        var frontCompTrend = Plot.Add.Scatter(
            compBalance.FrontTravel.ToArray(), compBalance.FrontTrend.ToArray());
        frontCompTrend.MarkerStyle.IsVisible = false;
        frontCompTrend.LineStyle.Color = FrontColor;
        frontCompTrend.LineStyle.Width = 2;

        var rearCompTrend = Plot.Add.Scatter(
            compBalance.RearTravel.ToArray(), compBalance.RearTrend.ToArray());
        rearCompTrend.MarkerStyle.IsVisible = false;
        rearCompTrend.LineStyle.Color = RearColor;
        rearCompTrend.LineStyle.Width = 2;

        // Rebound trend lines (Q4 — velocities are already negative)
        var frontRebTrend = Plot.Add.Scatter(
            rebBalance.FrontTravel.ToArray(), rebBalance.FrontTrend.ToArray());
        frontRebTrend.MarkerStyle.IsVisible = false;
        frontRebTrend.LineStyle.Color = FrontColor;
        frontRebTrend.LineStyle.Width = 2;

        var rearRebTrend = Plot.Add.Scatter(
            rebBalance.RearTravel.ToArray(), rebBalance.RearTrend.ToArray());
        rearRebTrend.MarkerStyle.IsVisible = false;
        rearRebTrend.LineStyle.Color = RearColor;
        rearRebTrend.LineStyle.Width = 2;

        // Legend
        var frontLegend = Plot.Add.Text("Front", 100, roundedMaxComp * 0.95);
        frontLegend.LabelFontColor = FrontColor;
        frontLegend.LabelFontSize = 12;
        frontLegend.LabelAlignment = Alignment.UpperRight;
        frontLegend.LabelOffsetX = -6;

        var rearLegend = Plot.Add.Text("Rear", 100, roundedMaxComp * 0.87);
        rearLegend.LabelFontColor = RearColor;
        rearLegend.LabelFontSize = 12;
        rearLegend.LabelAlignment = Alignment.UpperRight;
        rearLegend.LabelOffsetX = -6;
    }
}
