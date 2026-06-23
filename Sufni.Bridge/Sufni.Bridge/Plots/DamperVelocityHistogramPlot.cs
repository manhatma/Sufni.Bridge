using System.Collections.Generic;
using System.Linq;
using ScottPlot;
using ScottPlot.TickGenerators;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

public class DamperVelocityHistogramPlot(Plot plot) : TelemetryPlot(plot)
{
    private readonly List<Color> palette =
    [
        Color.FromHex("#3288bd"),
        Color.FromHex("#66c2a5"),
        Color.FromHex("#abdda4"),
        Color.FromHex("#e6f598"),
        Color.FromHex("#ffffbf"),
        Color.FromHex("#fee08b"),
        Color.FromHex("#fdae61"),
        Color.FromHex("#f46d43"),
        Color.FromHex("#d53e4f"),
        Color.FromHex("#9e0142"),
    ];

    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);

        SetTitle("Rear shaft velocity (damper domain)");

        Plot.Layout.Fixed(new PixelPadding(50, 24, 50, 40));

        Plot.Axes.Bottom.Label.Text = "Shaft velocity (mm/s)";
        Plot.Axes.Left.Label.Text = "Time (%)";

        var data = telemetryData.CalculateDamperVelocityHistogram();
        if (data.Bins.Count < 2)
            return;

        var step = data.Bins[1] - data.Bins[0];
        var maxY = 0.0;

        for (var i = 0; i < data.Values.Count; ++i)
        {
            double nextBarBase = 0;
            double colTotal = 0;
            for (var j = 0; j < TelemetryData.TravelBinsForVelocityHistogram; j++)
                colTotal += data.Values[i][j];
            if (colTotal > maxY) maxY = colTotal;

            for (var j = 0; j < TelemetryData.TravelBinsForVelocityHistogram; j++)
            {
                if (data.Values[i][j] == 0) continue;

                Plot.Add.Bar(new Bar
                {
                    Position = data.Bins[i] + step / 2.0,   // mm/s, centered on bin midpoint
                    ValueBase = nextBarBase,
                    Value = nextBarBase + data.Values[i][j],
                    FillColor = palette[j].WithOpacity(0.8),
                    LineColor = Colors.Black,
                    LineWidth = 0.5f,
                    Orientation = Orientation.Vertical,
                    Size = step * 0.95,
                });

                nextBarBase += data.Values[i][j];
            }
        }

        var yRangeTop = System.Math.Max(1.0, maxY) * 1.3;

        // Symmetric X axis around 0 (negative/positive equally scaled), consistent with the
        // wheel-velocity histograms. The limit covers the bulk of the data — sparse high-speed
        // outlier tails are clipped (like the wheel histogram's fixed range) so the fine bins
        // fill the plot and stay readable instead of being squashed into the centre.
        var limit = SymmetricMassLimit(data, step, 0.99);
        if (limit <= 0) limit = step;
        Plot.Axes.SetLimits(left: -limit, right: limit, bottom: 0, top: yRangeTop);
        // ~4 ticks per side at a round interval, so mm/s labels (e.g. "1.000") don't collide on phone widths.
        Plot.Axes.Bottom.TickGenerator = new NumericFixedInterval(NiceTickInterval(limit));

        Plot.Add.VerticalLine(0, 1f, Color.FromHex("#dddddd"), LinePattern.Dotted);

        // Normal reference overlay: X = shaft velocity (mm/s), Y = pdf (time %)
        var normalData = telemetryData.CalculateDamperNormalDistribution();
        var normal = Plot.Add.Scatter(
            normalData.Y.ToArray(),
            normalData.Pdf.ToArray());
        normal.Color = Color.FromHex("#d53e4f");
        normal.MarkerStyle.IsVisible = false;
        normal.LineStyle.Width = 3;
        normal.LineStyle.Pattern = LinePattern.Dotted;

        AddBinColorLegend(palette, -limit, limit, yRangeTop);

        var symmetry = TelemetryData.CalculateVelocityHistogramSymmetry(data);
        var label = Plot.Add.Text($"Sym: {symmetry:0.00}", -limit, yRangeTop * 0.97);
        label.LabelFontColor = RearColor;
        label.LabelFontSize = 10;
        label.LabelFontName = "Menlo";
        label.LabelAlignment = Alignment.UpperLeft;
        label.LabelOffsetX = 5;
        label.LabelBold = true;
        label.LabelBackgroundColor = Color.FromHex("#15191C").WithAlpha(220);
        label.LabelBorderColor = RearColor.WithAlpha(80);
        label.LabelBorderWidth = 1;
        label.LabelPadding = 5;
    }

    // Picks a round tick spacing (~4 divisions per side) so mm/s labels stay readable and
    // don't overlap, regardless of the bike's shaft-velocity range.
    private static double NiceTickInterval(double limit)
    {
        var raw = limit / 4.0;
        double[] steps = [50d, 100d, 200d, 250d, 500d, 1000d, 2000d, 5000d];
        foreach (var s in steps)
            if (s >= raw) return s;
        return 5000d;
    }

    // Smallest symmetric |velocity| whose [-limit, +limit] window covers `coverage` of the total
    // histogram mass — keeps rare high-speed outliers from blowing the axis out and squashing detail.
    private static double SymmetricMassLimit(StackedHistogramData data, double step, double coverage)
    {
        var total = data.Values.Sum(v => v.Sum());
        if (total <= 0)
            return System.Math.Max(System.Math.Abs(data.Bins.First()), System.Math.Abs(data.Bins.Last()));

        var allowedTrim = total * (1.0 - coverage);
        var ordered = new List<(double AbsMid, double Mass)>(data.Values.Count);
        for (var i = 0; i < data.Values.Count; i++)
            ordered.Add((System.Math.Abs(data.Bins[i] + step / 2.0), data.Values[i].Sum()));
        ordered.Sort((a, b) => b.AbsMid.CompareTo(a.AbsMid));

        var trimmed = 0.0;
        var limit = ordered.Count > 0 ? ordered[0].AbsMid : 0.0;
        foreach (var (absMid, mass) in ordered)
        {
            if (trimmed + mass > allowedTrim)
            {
                limit = absMid;
                break;
            }
            trimmed += mass;
        }
        return limit;
    }
}
