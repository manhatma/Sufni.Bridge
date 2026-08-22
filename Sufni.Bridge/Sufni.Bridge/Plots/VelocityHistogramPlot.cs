using System.Collections.Generic;
using System.Linq;
using MathNet.Numerics.Statistics;
using ScottPlot;
using ScottPlot.TickGenerators;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

public class VelocityHistogramPlot(Plot plot, SuspensionType type, bool powerWeighted = false, bool powerLogY = false) : TelemetryPlot(plot)
{
    // Display range ±2 m/s — matches VelocityDistributionComparisonPlot
    private const double VelocityLimitMs = 2.0;

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

    private static readonly Color StatColor = Color.FromHex("#FFD700");

    /// <summary>
    /// Stats box with avg/95th/max in mm/s — placed in the top padding area just below the title.
    /// </summary>
    private void AddStatsBox(TelemetryData telemetryData, double yRangeTop)
    {
        var stats = telemetryData.CalculateVelocityStatistics(type);
        var suspension = type == SuspensionType.Front ? telemetryData.Front : telemetryData.Rear;

        var compVels = suspension.Strokes.Compressions
            .SelectMany(s => suspension.Velocity[s.Start..(s.End + 1)])
            .ToList();
        var rebVels = suspension.Strokes.Rebounds
            .SelectMany(s => suspension.Velocity[s.Start..(s.End + 1)].Select(System.Math.Abs))
            .ToList();

        var p95Comp = compVels.Count > 0 ? compVels.Percentile(95) : 0.0;
        var p95Reb  = rebVels.Count  > 0 ? rebVels.Percentile(95)  : 0.0;

        // Monospace table (row label + Avg / 95th / Max columns). Non-breaking spaces keep
        // the columns aligned in ScottPlot's SVG text rendering. Headers are centered over
        // each column; values stay right-aligned. Rebound stays signed (negative).
        static string Num(double v, int w = 7) =>
            v.ToString("F1").PadLeft(w).Replace(' ', '\u00A0');
        static string Ctr(string s, int w = 7)
        {
            var left = (w - s.Length) / 2;
            return s.PadLeft(s.Length + left).PadRight(w).Replace(' ', '\u00A0');
        }
        static string Row(string s, int w = 6) =>
            s.PadRight(w).Replace(' ', '\u00A0');

        const string gap = "\u00A0";
        var statsText =
            $"{Row("[mm/s]")}{gap}{Ctr("Avg")}{gap}{Ctr("95th")}{gap}{Ctr("Max")}\n" +
            $"{Row("Comp")}{gap}{Num(stats.AverageCompression)}{gap}{Num(p95Comp)}{gap}{Num(stats.MaxCompression)}\n" +
            $"{Row("Reb")}{gap}{Num(stats.AverageRebound)}{gap}{Num(-p95Reb)}{gap}{Num(stats.MaxRebound)}";

        var box = Plot.Add.Text(statsText, VelocityLimitMs, yRangeTop * 0.97);
        box.LabelFontColor = StatColor;
        box.LabelFontSize = 9;
        box.LabelFontName = "Menlo";
        box.LabelAlignment = Alignment.UpperRight;
        box.LabelOffsetX = -5;
        box.LabelBold = true;
        box.LabelBackgroundColor = Color.FromHex("#15191C").WithAlpha(220);
        box.LabelBorderColor = StatColor.WithAlpha(80);
        box.LabelBorderWidth = 1;
        box.LabelPadding = 5;
    }

    private void AddSymmetryLabel(
        TelemetryData telemetryData,
        SuspensionType suspensionType,
        double deadBand,
        double yRangeTop)
    {
        var symmetry = telemetryData.CalculateVelocitySymmetry(
            suspensionType,
            Parameters.VelocityHistStep,
            deadBand);
        var color = type == SuspensionType.Front ? FrontColor : RearColor;
        var label = Plot.Add.Text(
            $"Sym: {symmetry:0.00}",
            -VelocityLimitMs,
            yRangeTop * 0.97);
        label.LabelFontColor = color;
        label.LabelFontSize = 10;
        label.LabelFontName = "Menlo";
        label.LabelAlignment = Alignment.UpperLeft;
        label.LabelOffsetX = 5;
        label.LabelBold = true;
        label.LabelBackgroundColor = Color.FromHex("#15191C").WithAlpha(220);
        label.LabelBorderColor = color.WithAlpha(80);
        label.LabelBorderWidth = 1;
        label.LabelPadding = 5;
    }

    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);

        SetTitle(type == SuspensionType.Front
            ? "Front wheel velocity"
            : "Rear wheel velocity");

        if (powerWeighted)
        {
            LoadPowerWeightedData(telemetryData);
            return;
        }

        Plot.Layout.Fixed(new PixelPadding(50, 24, 50, 40));

        Plot.Axes.Bottom.Label.Text = "Velocity (m/s)";
        Plot.Axes.Left.Label.Text = "Time (%)";

        var deadBand = type == SuspensionType.Front
            ? telemetryData.FrontVelocityDeadBand()
            : telemetryData.RearWheelVelocityDeadBand();
        var data = telemetryData.CalculateVelocityHistogram(type);
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
                    Position = (data.Bins[i] + step / 2.0) / 1000.0,   // mm/s → m/s, centered on bin midpoint
                    ValueBase = nextBarBase,
                    Value = nextBarBase + data.Values[i][j],
                    FillColor = palette[j].WithOpacity(0.8),
                    LineColor = Colors.Black,
                    LineWidth = 0.5f,
                    Orientation = Orientation.Vertical,  // velocity on X, time% on Y
                    Size = step / 1000.0 * 0.95,         // mm/s → m/s
                });

                nextBarBase += data.Values[i][j];
            }
        }

        var yRangeTop = System.Math.Max(1.0, maxY) * 1.3;

        // X: ±2 m/s with 0.5 m/s ticks — same scale as VelocityDistributionComparison
        Plot.Axes.SetLimits(left: -VelocityLimitMs, right: VelocityLimitMs, bottom: 0, top: yRangeTop);
        Plot.Axes.Bottom.TickGenerator = new NumericFixedInterval(0.5);

        Plot.Add.VerticalLine(0, 1f, Color.FromHex("#dddddd"), LinePattern.Dotted);

        AddBinColorLegend(palette, -VelocityLimitMs, VelocityLimitMs, yRangeTop);

        AddSymmetryLabel(telemetryData, type, deadBand, yRangeTop);
        AddStatsBox(telemetryData, yRangeTop);
    }

    private void LoadPowerWeightedData(TelemetryData telemetryData)
    {
        Plot.Layout.Fixed(new PixelPadding(50, 24, 50, 40));

        Plot.Axes.Bottom.Label.Text = "Velocity (m/s)";
        Plot.Axes.Left.Label.Text = powerLogY ? "Rel. damper power (%) — log" : "Rel. damper power (%)";

        var data = telemetryData.CalculateVelocityHistogram(type);
        if (data.Bins.Count < 2)
            return;
        var step = data.Bins[1] - data.Bins[0];
        var n = data.Values.Count;

        // Power weight ∝ v²·t per source bin, plus each column's total energy.
        var mids = new double[n];
        var colEnergy = new double[n];
        var totalEnergy = 0.0;
        for (var i = 0; i < n; i++)
        {
            var mid = data.Bins[i] + step / 2.0;
            mids[i] = mid;
            var vMs = mid / 1000.0;
            var factor = vMs * vMs;
            double sum = 0;
            for (var j = 0; j < TelemetryData.TravelBinsForVelocityHistogram; j++)
                sum += data.Values[i][j] * factor;
            colEnergy[i] = sum;
            totalEnergy += sum;
        }
        if (totalEnergy <= 0)
            return;

        // Independent left/right limits — full extent per side (asymmetric), so a lopsided
        // comp/reb spread doesn't leave half the frame empty.
        var (left, right) = AsymmetricEnergyLimits(mids, colEnergy, 300.0);

        // Native bin width (no coarsening) — only the X limits are made asymmetric.
        var binWidth = step;
        var aggregated = AggregatePower(data, mids, binWidth);

        var scale = 100.0 / totalEnergy;
        var axisLimit = System.Math.Max(left, right);
        var maxTotal = 0.0;
        foreach (var (index, segments) in aggregated)
        {
            var center = (index + 0.5) * binWidth;
            double colTotal = 0;
            for (var j = 0; j < TelemetryData.TravelBinsForVelocityHistogram; j++)
                colTotal += segments[j] * scale;
            if (System.Math.Abs(center) <= axisLimit && colTotal > maxTotal)
                maxTotal = colTotal;

            double nextBarBase = 0;
            for (var j = 0; j < TelemetryData.TravelBinsForVelocityHistogram; j++)
            {
                var value = segments[j] * scale;
                if (value == 0) continue;

                Plot.Add.Bar(new Bar
                {
                    Position = center / 1000.0,
                    ValueBase = PowerY(nextBarBase),
                    Value = PowerY(nextBarBase + value),
                    FillColor = palette[j].WithOpacity(0.8),
                    LineColor = Colors.Black,
                    LineWidth = 0.5f,
                    Orientation = Orientation.Vertical,
                    Size = binWidth / 1000.0 * 0.95,
                });

                nextBarBase += value;
            }
        }

        var leftMs = -left / 1000.0;
        var rightMs = right / 1000.0;
        var yRangeTop = powerLogY
            ? PowerLogTransform(maxTotal) * 1.08
            : System.Math.Max(0.5, maxTotal) * 1.15;
        Plot.Axes.SetLimits(left: leftMs, right: rightMs, bottom: 0, top: yRangeTop);
        Plot.Axes.Bottom.TickGenerator = new NumericFixedInterval(NicePowerTickInterval(System.Math.Max(rightMs, -leftMs)));
        if (powerLogY)
            ApplyPowerLogYTicks(maxTotal);

        Plot.Add.VerticalLine(0, 1f, Color.FromHex("#dddddd"), LinePattern.Dotted);
        AddBinColorLegend(palette, leftMs, rightMs, yRangeTop, xFraction: 0.90, yLoFraction: 0.30, yHiFraction: 0.70, labelsLeft: true);
    }

    private double PowerY(double y) => powerLogY ? PowerLogTransform(y) : y;

    // Independent per-side X limits: the full velocity extent of each side (outermost bin
    // that holds data), clamped to a floor so a near-empty side doesn't collapse. No trimming.
    private static (double left, double right) AsymmetricEnergyLimits(double[] mids, double[] energy, double floor)
    {
        double Side(bool positive)
        {
            var limit = 0.0;
            for (var i = 0; i < mids.Length; i++)
                if ((mids[i] >= 0) == positive && energy[i] > 0)
                    limit = System.Math.Max(limit, System.Math.Abs(mids[i]));
            return System.Math.Max(limit, floor);
        }
        return (Side(false), Side(true));
    }

    // Aggregate power-weighted travel segments into display bins of binWidth; key = floor(mid/binWidth) so 0 is a boundary.
    private static SortedDictionary<int, double[]> AggregatePower(StackedHistogramData data, double[] mids, double binWidth)
    {
        var agg = new SortedDictionary<int, double[]>();
        for (var i = 0; i < data.Values.Count; i++)
        {
            var index = (int)System.Math.Floor(mids[i] / binWidth);
            if (!agg.TryGetValue(index, out var seg))
            {
                seg = new double[TelemetryData.TravelBinsForVelocityHistogram];
                agg[index] = seg;
            }
            var vMs = mids[i] / 1000.0;
            var factor = vMs * vMs;
            for (var j = 0; j < TelemetryData.TravelBinsForVelocityHistogram; j++)
                seg[j] += data.Values[i][j] * factor;
        }
        return agg;
    }

    private static double NicePowerTickInterval(double limit)
    {
        var raw = limit / 4.0;
        double[] steps = [0.05, 0.1, 0.2, 0.25, 0.5, 1.0, 2.0, 5.0];
        foreach (var step in steps)
            if (step >= raw) return step;
        return 5.0;
    }
}
