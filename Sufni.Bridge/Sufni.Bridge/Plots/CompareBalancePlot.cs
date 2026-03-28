using System;
using System.Collections.Generic;
using System.Linq;
using ScottPlot;
using ScottPlot.TickGenerators;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

public class CompareBalancePlot(Plot plot) : SufniPlot(plot)
{
    public void LoadMultipleSessions(List<(TelemetryData data, Color color, LinePattern pattern, string name)> sessions)
    {
        SetTitle("Balance");
        Plot.Axes.Bottom.Label.Text = "Suspension travel (%)";
        Plot.Axes.Bottom.Label.ForeColor = Color.FromHex("#D0D0D0");
        Plot.Axes.Left.Label.Text = "Velocity (mm/s)";
        Plot.Axes.Left.Label.ForeColor = Color.FromHex("#D0D0D0");
        Plot.Layout.Fixed(new PixelPadding(76, 14, 50, 40));

        double maxComp = 0;
        double maxReb = 0;

        // First pass: determine axis limits across all sessions
        foreach (var (data, _, _, _) in sessions)
        {
            if (!data.Front.Present || !data.Rear.Present) continue;

            var compBalance = data.CalculateBalance(BalanceType.Compression);
            var rebBalance = data.CalculateBalance(BalanceType.Rebound);

            var sessionMaxComp = Math.Max(
                compBalance.FrontTrend.Select(Math.Abs).DefaultIfEmpty(0).Max(),
                compBalance.RearTrend.Select(Math.Abs).DefaultIfEmpty(0).Max());
            var sessionMaxReb = Math.Max(
                rebBalance.FrontTrend.Select(Math.Abs).DefaultIfEmpty(0).Max(),
                rebBalance.RearTrend.Select(Math.Abs).DefaultIfEmpty(0).Max());

            if (sessionMaxComp > maxComp) maxComp = sessionMaxComp;
            if (sessionMaxReb > maxReb) maxReb = sessionMaxReb;
        }

        var roundedMaxComp = (int)Math.Ceiling(maxComp / 100.0) * 100;
        var roundedMaxReb = (int)Math.Ceiling(maxReb / 100.0) * 100;

        Plot.Axes.SetLimits(0, 100, -roundedMaxReb, roundedMaxComp);

        var tickInterval = (int)Math.Ceiling(Math.Max(maxComp, maxReb) / 5 / 100.0) * 100;
        Plot.Axes.Left.TickGenerator = new NumericFixedInterval(tickInterval);
        Plot.Axes.Bottom.TickGenerator = new NumericManual(
            [0.0, 10.0, 20.0, 30.0, 40.0, 50.0, 60.0, 70.0, 80.0, 90.0, 100.0],
            ["0", "10", "20", "30", "40", "50", "60", "70", "80", "90", "100"]);

        // Zero velocity reference line
        Plot.Add.HorizontalLine(0, 1f, Color.FromHex("#dddddd"), LinePattern.Dotted);

        // Second pass: draw trend lines for each session
        foreach (var (data, color, _, name) in sessions)
        {
            if (!data.Front.Present || !data.Rear.Present) continue;

            var compBalance = data.CalculateBalance(BalanceType.Compression);
            var rebBalance = data.CalculateBalance(BalanceType.Rebound);

            // Front trends: Solid
            var frontCompTrend = Plot.Add.Scatter(
                compBalance.FrontTravel.ToArray(), compBalance.FrontTrend.ToArray());
            frontCompTrend.MarkerStyle.IsVisible = false;
            frontCompTrend.LineStyle.Color = color;
            frontCompTrend.LineStyle.Pattern = LinePattern.Solid;
            frontCompTrend.LineStyle.Width = 2;

            var frontRebTrend = Plot.Add.Scatter(
                rebBalance.FrontTravel.ToArray(), rebBalance.FrontTrend.ToArray());
            frontRebTrend.MarkerStyle.IsVisible = false;
            frontRebTrend.LineStyle.Color = color;
            frontRebTrend.LineStyle.Pattern = LinePattern.Solid;
            frontRebTrend.LineStyle.Width = 2;

            // Rear trends: Dashed
            var rearCompTrend = Plot.Add.Scatter(
                compBalance.RearTravel.ToArray(), compBalance.RearTrend.ToArray());
            rearCompTrend.MarkerStyle.IsVisible = false;
            rearCompTrend.LineStyle.Color = color;
            rearCompTrend.LineStyle.Pattern = LinePattern.Dashed;
            rearCompTrend.LineStyle.Width = 2;

            var rearRebTrend = Plot.Add.Scatter(
                rebBalance.RearTravel.ToArray(), rebBalance.RearTrend.ToArray());
            rearRebTrend.MarkerStyle.IsVisible = false;
            rearRebTrend.LineStyle.Color = color;
            rearRebTrend.LineStyle.Pattern = LinePattern.Dashed;
            rearRebTrend.LineStyle.Width = 2;
        }

        // Legend centered vertically (middle-right), shifted up for distance from y=0
        var legendStep = Math.Max(roundedMaxComp, roundedMaxReb) * 0.08;
        var midY = (roundedMaxComp - roundedMaxReb) / 2.0;
        var totalItems = sessions.Count + 1; // session names + 1 hint line
        var legendY = midY + (totalItems / 2.0) * legendStep + legendStep;

        // Combined front/rear hint line above session names
        var hint = Plot.Add.Text("— Front   - - Rear", 100, legendY);
        hint.LabelFontColor = Color.FromHex("#808080");
        hint.LabelFontSize = 10;
        hint.LabelAlignment = Alignment.UpperRight;
        hint.LabelOffsetX = -6;

        for (var i = 0; i < sessions.Count; i++)
        {
            var (_, color, _, name) = sessions[i];
            var label = Plot.Add.Text(name, 100, legendY - (i + 1) * legendStep);
            label.LabelFontColor = color;
            label.LabelFontSize = 12;
            label.LabelAlignment = Alignment.UpperRight;
            label.LabelOffsetX = -6;
        }
    }
}
