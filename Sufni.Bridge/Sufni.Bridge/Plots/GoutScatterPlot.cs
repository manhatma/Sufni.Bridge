using System;
using ScottPlot;
using ScottPlot.TickGenerators;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

/// <summary>
/// G-out load symmetry: each paired G-out event plotted as rear-vs-front used travel (% of
/// available). Points on the dashed 1:1 line are balanced; the tolerance band (±TelemetryData
/// .GoutAsymmetryThresholdPp pp, the shared asymmetry threshold) brackets the "acceptable"
/// asymmetry. The stats box summarises how often, and how far, the loading skews front or rear.
/// </summary>
public class GoutScatterPlot(Plot plot) : TelemetryPlot(plot)
{
    private static readonly Color StatColor = Color.FromHex("#FFD700");

    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);

        SetTitle("G-out load symmetry");
        Plot.Layout.Fixed(new PixelPadding(65 - (int)Plot.Axes.Left.TickLabelStyle.FontSize, 24, 50, 40));
        Plot.Axes.Bottom.Label.Text = "Rear travel used (%)";
        Plot.Axes.Left.Label.Text = "Front travel used (%)";

        var events = telemetryData.CalculateGoutEvents();
        if (events is null || events.Count == 0)
        {
            AddLabel("Not enough G-out events", 0.5, 0.5, 0, 0, Alignment.MiddleCenter, "#aaaaaa");
            return;
        }

        var n = events.Count;
        var rear = new double[n];
        var front = new double[n];
        for (var i = 0; i < n; i++)
        {
            rear[i] = events[i].RearPct;
            front[i] = events[i].FrontPct;
        }

        // Event points (gold, semi-transparent fill).
        var scatter = Plot.Add.Scatter(rear, front);
        scatter.LineStyle.IsVisible = false;
        scatter.MarkerStyle.FillColor = Color.FromHex("#e0b83a").WithOpacity(0.6);
        scatter.MarkerStyle.LineColor = Color.FromHex("#e0b83a").WithOpacity(0.6);
        scatter.MarkerStyle.Size = 5f;

        // 1:1 reference line (dashed, rear colour).
        var oneToOne = Plot.Add.Scatter(new[] { 0.0, 100.0 }, new[] { 0.0, 100.0 });
        oneToOne.MarkerStyle.IsVisible = false;
        oneToOne.LineStyle.Color = RearColor;
        oneToOne.LineStyle.Width = 2;
        oneToOne.LineStyle.Pattern = LinePattern.Dashed;

        // Tolerance band (dashed grey), ± TelemetryData.GoutAsymmetryThresholdPp pp around the 1:1 line.
        var thr = TelemetryData.GoutAsymmetryThresholdPp;
        var upper = Plot.Add.Scatter(new[] { 0.0, 100.0 - thr }, new[] { thr, 100.0 });
        upper.MarkerStyle.IsVisible = false;
        upper.LineStyle.Color = Color.FromHex("#888888");
        upper.LineStyle.Width = 1.5f;
        upper.LineStyle.Pattern = LinePattern.Dashed;

        var lower = Plot.Add.Scatter(new[] { thr, 100.0 }, new[] { 0.0, 100.0 - thr });
        lower.MarkerStyle.IsVisible = false;
        lower.LineStyle.Color = Color.FromHex("#888888");
        lower.LineStyle.Width = 1.5f;
        lower.LineStyle.Pattern = LinePattern.Dashed;

        // Statistics on the front−rear difference.
        var diffs = new double[n];
        var absDiffs = new double[n];
        double sum = 0;
        var asym = 0;
        for (var i = 0; i < n; i++)
        {
            var d = front[i] - rear[i];
            diffs[i] = d;
            absDiffs[i] = Math.Abs(d);
            sum += d;
            if (Math.Abs(d) > thr) asym++;
        }
        var meanDiff = sum / n;
        double sumSq = 0;
        for (var i = 0; i < n; i++)
        {
            var e = diffs[i] - meanDiff;
            sumSq += e * e;
        }
        var std = Math.Sqrt(sumSq / n);
        var pct = 100.0 * asym / n;

        Array.Sort(absDiffs);
        var p95Index = (int)Math.Ceiling(0.95 * (n - 1));
        if (p95Index < 0) p95Index = 0;
        if (p95Index > n - 1) p95Index = n - 1;
        var p95 = absDiffs[p95Index];
        var maxAbs = absDiffs[n - 1];
        var medAbs = n % 2 == 1 ? absDiffs[n / 2] : 0.5 * (absDiffs[n / 2 - 1] + absDiffs[n / 2]);

        // Gold stats box (upper-left, away from the points), Menlo. Below the reliable-event
        // threshold P95≈Max and std is dominated by single outliers, so flag the box "indicative"
        // and fall back to the robust median + max spread instead of std / P95.
        var lowN = n < TelemetryData.GoutMinReliableEvents;
        var statsText = lowN
            ? $"indicative (N={n})\n" +
              $"asymmetric: {pct:0}%\n" +
              $"median |F−R|: {medAbs:0.0} pp\n" +
              $"max |F−R|: {maxAbs:0.0} pp"
            : $"N events: {n}\n" +
              $"asymmetric: {pct:0}%\n" +
              $"std(F−R): {std:0.0} pp\n" +
              $"P95 |F−R|: {p95:0.0} pp";

        var statsLabel = Plot.Add.Text(statsText, 0, 100);
        statsLabel.LabelFontColor = StatColor;
        statsLabel.LabelFontSize = 9;
        statsLabel.LabelFontName = "Menlo";
        statsLabel.LabelAlignment = Alignment.UpperLeft;
        statsLabel.LabelOffsetX = 10;
        statsLabel.LabelOffsetY = 6;
        statsLabel.LabelBold = true;
        statsLabel.LabelBackgroundColor = Color.FromHex("#15191C").WithAlpha(220);
        statsLabel.LabelBorderColor = StatColor.WithAlpha(80);
        statsLabel.LabelBorderWidth = 1;
        statsLabel.LabelPadding = 5;

        Plot.Axes.SetLimits(0, 100, 0, 100);
        Plot.Axes.Bottom.TickGenerator = new NumericManual(
            [0.0, 10.0, 20.0, 30.0, 40.0, 50.0, 60.0, 70.0, 80.0, 90.0, 100.0],
            ["0", "10", "20", "30", "40", "50", "60", "70", "80", "90", "100"]);
        Plot.Axes.Left.TickGenerator = new NumericManual(
            [0.0, 10.0, 20.0, 30.0, 40.0, 50.0, 60.0, 70.0, 80.0, 90.0, 100.0],
            ["0", "10", "20", "30", "40", "50", "60", "70", "80", "90", "100"]);
    }
}
