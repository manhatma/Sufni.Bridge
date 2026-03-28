using System;
using System.Collections.Generic;
using System.Linq;
using ScottPlot;
using ScottPlot.TickGenerators;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

public class CompareBalanceTypePlot(Plot plot, BalanceType type) : SufniPlot(plot)
{
    public void LoadMultipleSessions(List<(TelemetryData data, Color color, LinePattern pattern, string name)> sessions)
    {
        var title = type == BalanceType.Compression ? "Compression balance" : "Rebound balance";
        SetTitle(title);
        Plot.Axes.Bottom.Label.Text = "Suspension travel (%)";
        Plot.Axes.Bottom.Label.ForeColor = Color.FromHex("#D0D0D0");
        Plot.Axes.Left.Label.Text = type == BalanceType.Compression ? "Compression velocity (mm/s)" : "Rebound velocity (mm/s)";
        Plot.Axes.Left.Label.ForeColor = Color.FromHex("#D0D0D0");
        var leftPad = type == BalanceType.Rebound ? 76 : 70;
        Plot.Layout.Fixed(new PixelPadding(leftPad, 14, 50, 40));

        double maxVelocity = 0;

        // First pass: determine axis limits across all sessions
        foreach (var (data, _, _, _) in sessions)
        {
            if (!data.Front.Present || !data.Rear.Present) continue;

            var balance = data.CalculateBalance(type);
            var sessionMax = Math.Max(
                balance.FrontTrend.Select(Math.Abs).DefaultIfEmpty(0).Max(),
                balance.RearTrend.Select(Math.Abs).DefaultIfEmpty(0).Max());

            if (sessionMax > maxVelocity) maxVelocity = sessionMax;
        }

        var roundedMaxVelocity = (int)Math.Ceiling(maxVelocity / 100.0) * 100;

        if (type == BalanceType.Rebound)
            Plot.Axes.SetLimits(0, 100, 0, -roundedMaxVelocity);
        else
            Plot.Axes.SetLimits(0, 100, 0, roundedMaxVelocity);

        var tickInterval = (int)Math.Ceiling(maxVelocity / 5 / 100.0) * 100;
        Plot.Axes.Left.TickGenerator = new NumericFixedInterval(tickInterval);
        Plot.Axes.Bottom.TickGenerator = new NumericManual(
            [0.0, 10.0, 20.0, 30.0, 40.0, 50.0, 60.0, 70.0, 80.0, 90.0, 100.0],
            ["0", "10", "20", "30", "40", "50", "60", "70", "80", "90", "100"]);

        // Second pass: draw trend lines for each session
        foreach (var (data, color, _, name) in sessions)
        {
            if (!data.Front.Present || !data.Rear.Present) continue;

            var balance = data.CalculateBalance(type);

            // Front trends: Solid
            var frontTrend = Plot.Add.Scatter(
                balance.FrontTravel.ToArray(), balance.FrontTrend.ToArray());
            frontTrend.MarkerStyle.IsVisible = false;
            frontTrend.LineStyle.Color = color;
            frontTrend.LineStyle.Pattern = LinePattern.Solid;
            frontTrend.LineStyle.Width = 2;

            // Rear trends: Dashed
            var rearTrend = Plot.Add.Scatter(
                balance.RearTravel.ToArray(), balance.RearTrend.ToArray());
            rearTrend.MarkerStyle.IsVisible = false;
            rearTrend.LineStyle.Color = color;
            rearTrend.LineStyle.Pattern = LinePattern.Dashed;
            rearTrend.LineStyle.Width = 2;
        }

        // Legend in lower-right corner
        var legendStep = Math.Abs(roundedMaxVelocity * 0.08);
        var legendBase = type == BalanceType.Compression
            ? legendStep * (sessions.Count + 1)
            : -legendStep * (sessions.Count + 1);

        // Combined front/rear hint line above session names
        var hintY = type == BalanceType.Compression
            ? legendBase + sessions.Count * legendStep
            : legendBase - sessions.Count * legendStep;
        var hint = Plot.Add.Text("— Front   - - Rear", 100, hintY);
        hint.LabelFontColor = Color.FromHex("#808080");
        hint.LabelFontSize = 10;
        hint.LabelAlignment = Alignment.UpperRight;
        hint.LabelOffsetX = -6;

        // Session names below hint
        for (var i = 0; i < sessions.Count; i++)
        {
            var (_, color, _, name) = sessions[i];
            var labelY = type == BalanceType.Compression
                ? legendBase + (sessions.Count - 1 - i) * legendStep
                : legendBase - (sessions.Count - 1 - i) * legendStep;
            var label = Plot.Add.Text(name, 100, labelY);
            label.LabelFontColor = color;
            label.LabelFontSize = 12;
            label.LabelAlignment = Alignment.UpperRight;
            label.LabelOffsetX = -6;
        }
    }
}
