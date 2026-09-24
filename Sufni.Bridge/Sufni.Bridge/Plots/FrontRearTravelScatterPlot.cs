using System;
using System.Collections.Generic;
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

        AddDensityMarkers(rear, front);

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

    // Marker grid in % of travel: 0.25 % is below one on-screen pixel at the cached render size
    // (~0.8 px x 0.6 px data area) and below the 1.5 px marker, also at 2x PDF export.
    private const double CellPercent = 0.25;
    private const double MarkerAlpha = 0.4;
    // 1 - 0.6^8 = 0.983: more stacked markers are visually opaque.
    private const int MaxStack = 8;

    /// <summary>
    /// Draws the scatter with one marker per occupied grid cell instead of one per sample.
    /// Each marker gets the opacity that k stacked 0.4-alpha markers composite to (1 - 0.6^k);
    /// same-colour alpha compositing is order-independent, so the image matches the per-sample
    /// plot up to a sub-pixel position shift (each marker sits at its cell's sample mean).
    /// Per-sample markers made this the largest cached SVG (~6 MB, ~0.6 s SvgSource parse on
    /// every session open); per-cell markers cut it to ~2 MB / ~0.2 s.
    /// </summary>
    private void AddDensityMarkers(double[] rear, double[] front)
    {
        // Per cell: sample count and coordinate sums. The marker sits at the cell mean, not the
        // cell centre — snapping to centres draws a visible lattice in dense regions.
        var cells = new Dictionary<(int x, int y), (int n, double sx, double sy)>();
        for (var i = 0; i < rear.Length; i++)
        {
            if (!double.IsFinite(rear[i]) || !double.IsFinite(front[i])) continue;
            var key = ((int)Math.Floor(rear[i] / CellPercent), (int)Math.Floor(front[i] / CellPercent));
            var c = cells.GetValueOrDefault(key);
            cells[key] = (c.n + 1, c.sx + rear[i], c.sy + front[i]);
        }

        var xs = new List<double>[MaxStack];
        var ys = new List<double>[MaxStack];
        for (var k = 0; k < MaxStack; k++) { xs[k] = []; ys[k] = []; }
        foreach (var (n, sx, sy) in cells.Values)
        {
            var k = Math.Min(n, MaxStack) - 1;
            xs[k].Add(sx / n);
            ys[k].Add(sy / n);
        }

        for (var k = 0; k < MaxStack; k++)
        {
            if (xs[k].Count == 0) continue;
            var color = Color.FromHex("#d8d8d8").WithOpacity(1.0 - Math.Pow(1.0 - MarkerAlpha, k + 1));
            var scatter = Plot.Add.Scatter(xs[k].ToArray(), ys[k].ToArray());
            scatter.LineStyle.IsVisible = false;
            scatter.MarkerStyle.FillColor = color;
            scatter.MarkerStyle.LineColor = color;
            scatter.MarkerStyle.Size = 1.5f;
        }
    }
}
