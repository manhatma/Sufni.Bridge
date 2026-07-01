using System;
using ScottPlot;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

/// <summary>
/// Chassis pitch over time, derived from the lag-corrected difference between rear and front
/// suspension travel mapped through the wheelbase. Nose-down is positive. An optional discipline
/// reference band (expectedMinDeg..expectedMaxDeg) marks the target pitch range; otherwise the
/// neutral 0° line is the only reference.
/// </summary>
public class PitchBalancePlot(Plot plot, double? expectedMinDeg, double? expectedMaxDeg) : TelemetryPlot(plot)
{
    private static readonly Color StatColor = Color.FromHex("#FFD700");

    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);

        SetTitle("Chassis pitch over time");
        Plot.Layout.Fixed(new PixelPadding(55, 14, 50, 40));
        Plot.Axes.Bottom.Label.Text = "Time (s)";
        Plot.Axes.Left.Label.Text = "Pitch (°, nose-down +)";

        var pitch = telemetryData.CalculatePitchDegrees();
        if (pitch is not { Length: > 0 })
        {
            AddLabel("Pitch needs front, rear and wheelbase", 0.5, 0.5, 0, 0, Alignment.MiddleCenter, "#aaaaaa");
            return;
        }

        var period = 1.0 / telemetryData.SampleRate;
        var maxDuration = pitch.Length * period;

        // Displayed statistics (μ, σ, P5/P95, extremes) come from the active-window pitch stats —
        // the union of both ends' active stroke windows, matching dynamic SAG windowing — so idle
        // sections don't dilute them. CalculatePitchStatistics returns null exactly when
        // CalculatePitchDegrees does, so this is unreachable when pitch above is non-empty.
        var stats = telemetryData.CalculatePitchStatistics()!.Value;
        var (mean, std, p5, p95, min, max) = stats;

        // Full-series min/max, used only for the Y-axis limits below so the drawn trace (which
        // still spans the whole series, idle included) always fits on screen.
        double fullMin = double.PositiveInfinity, fullMax = double.NegativeInfinity;
        for (var i = 0; i < pitch.Length; i++)
        {
            if (pitch[i] < fullMin) fullMin = pitch[i];
            if (pitch[i] > fullMax) fullMax = pitch[i];
        }

        // Expected discipline band first (drawn behind the trace) as a filled translucent green
        // region — the evaluation reference in place of the bare 0° line.
        var haveBand = expectedMinDeg.HasValue && expectedMaxDeg.HasValue;
        double bandLo = 0, bandHi = 0;
        if (haveBand)
        {
            bandLo = Math.Min(expectedMinDeg!.Value, expectedMaxDeg!.Value);
            bandHi = Math.Max(expectedMinDeg!.Value, expectedMaxDeg!.Value);
            var band = Plot.Add.Rectangle(0, maxDuration, bandLo, bandHi);
            band.FillStyle.Color = Color.FromHex("#6CC44A").WithAlpha(40);
            band.LineStyle.IsVisible = false;
        }

        var sig = Plot.Add.Signal(pitch, period);
        sig.Color = Color.FromHex("#c994c7").WithAlpha(70);
        sig.LineWidth = 1;

        // Bold zero-phase readability trend (~1.5 Hz pitch-mode / body band) drawn on top of the
        // faint raw trace. Statistics below come from the active-window stats computed above,
        // not from this trend.
        var trend = SmoothZeroPhase(pitch, telemetryData.SampleRate, TrendSmoothHz);
        var trendSig = Plot.Add.Signal(trend, period);
        trendSig.Color = Color.FromHex("#E15FB8");
        trendSig.LineWidth = 1.0f;

        // Neutral 0° reference — very faint, unlabelled (the green band is the reference now).
        Plot.Add.HorizontalLine(0, 1f, Color.FromHex("#dddddd").WithAlpha(70), LinePattern.Dotted);

        // P5 / P95 spread — thin grey dotted, labelled at the left edge so they read as a spread.
        Plot.Add.HorizontalLine(p5, 1f, Color.FromHex("#888888"), LinePattern.Dotted);
        Plot.Add.HorizontalLine(p95, 1f, Color.FromHex("#888888"), LinePattern.Dotted);
        AddLabel("P5", 0, p5, 6, -3, Alignment.UpperLeft, "#888888");
        AddLabel("P95", 0, p95, 6, 3, Alignment.LowerLeft, "#888888");

        // Expected-band label sitting on top of the green region.
        if (haveBand)
            AddLabel("expected", 0, bandHi, 10, 4, Alignment.LowerLeft, "#6CC44A");

        // Mean (μ) as a clear solid white line + label at the right edge — the dominant reference.
        Plot.Add.HorizontalLine(mean, 1.6f, Color.FromHex("#ffffff"), LinePattern.Solid);
        AddLabel($"μ={mean:0.0}°", maxDuration, mean, -10, mean >= 0 ? 5 : -5,
            mean >= 0 ? Alignment.UpperRight : Alignment.LowerRight, "#ffffff");

        // Y limits: fit to the FULL drawn trace (idle included) with 5% margin, but always keep
        // μ and the expected band visible.
        var lo = fullMin;
        var hi = fullMax;
        if (haveBand)
        {
            lo = Math.Min(lo, bandLo);
            hi = Math.Max(hi, bandHi);
        }
        lo = Math.Min(lo, mean);
        hi = Math.Max(hi, mean);
        var span = Math.Max(hi - lo, 1e-9);
        var bottom = lo - span * 0.05;
        var top = hi + span * 0.05;
        Plot.Axes.SetLimitsY(bottom: bottom, top: top);
        Plot.Axes.SetLimitsX(left: 0, right: maxDuration);

        // No τ / lag annotation: the front→rear traversal lag is heave-dominated and rarely
        // determinable on real trails, so showing a number (or "n/a") here misleads more than it
        // informs. τ is still applied internally to de-lag the pitch where it IS determinable.

        // Gold stats box (upper-right), Menlo, mirroring VelocityTimeCroppedPlot.
        var statsText =
            $"μ:   {mean:+0.0;-0.0}°\n" +
            $"σ:   {std:0.00}°\n" +
            $"P5–95: {p5:+0.0;-0.0}…{p95:+0.0;-0.0}°\n" +
            $"nose-dive max: {max:+0.0;-0.0;0.0}°\n" +
            $"squat max: {min:+0.0;-0.0;0.0}°";

        var statsLabel = Plot.Add.Text(statsText, maxDuration, top);
        statsLabel.LabelFontColor = StatColor;
        statsLabel.LabelFontSize = 9;
        statsLabel.LabelFontName = "Menlo";
        statsLabel.LabelAlignment = Alignment.UpperRight;
        statsLabel.LabelOffsetX = -10;
        statsLabel.LabelOffsetY = 6;
        statsLabel.LabelBold = true;
        statsLabel.LabelBackgroundColor = Color.FromHex("#15191C").WithAlpha(220);
        statsLabel.LabelBorderColor = StatColor.WithAlpha(80);
        statsLabel.LabelBorderWidth = 1;
        statsLabel.LabelPadding = 5;
    }

    // Zero-phase readability trend: 3 cascaded centered moving averages tuned to ~1.5 Hz
    // (chassis pitch-mode / body band). Mirrors TelemetryData.LowPassZeroPhase; applied to the
    // already-4-Hz pitch series purely for a legible overlaid trend line.
    private const double TrendSmoothHz = 1.5;

    private static double[] SmoothZeroPhase(double[] x, int sampleRate, double cutoffHz)
    {
        if (x.Length < 3 || sampleRate <= 0 || cutoffHz <= 0) return (double[])x.Clone();
        var radius = (int)Math.Round(0.13 * sampleRate / cutoffHz);
        if (radius < 1) return (double[])x.Clone();
        var y = MovingAverageCentered(x, radius);
        y = MovingAverageCentered(y, radius);
        y = MovingAverageCentered(y, radius);
        return y;
    }

    private static double[] MovingAverageCentered(double[] x, int radius)
    {
        var n = x.Length;
        var y = new double[n];
        var prefix = new double[n + 1];
        for (var i = 0; i < n; i++) prefix[i + 1] = prefix[i] + x[i];
        for (var i = 0; i < n; i++)
        {
            var lo = Math.Max(0, i - radius);
            var hi = Math.Min(n - 1, i + radius);
            y[i] = (prefix[hi + 1] - prefix[lo]) / (hi - lo + 1);
        }
        return y;
    }
}
