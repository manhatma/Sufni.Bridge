using System;
using ScottPlot;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

public class AccelerationTimeCroppedPlot(Plot plot, SuspensionType type,
    double? windowStartSeconds = null, double? windowEndSeconds = null) : TelemetryPlot(plot)
{
    private const double GravityMmPerS2 = 9806.65;
    private static readonly Color StatColor = Color.FromHex("#FFD700");

    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);

        var prefix = type == SuspensionType.Front ? "Front" : "Rear";
        SetTitle($"{prefix} acceleration over time");
        Plot.Layout.Fixed(new PixelPadding(55, 14, 50, 40));
        Plot.Axes.Bottom.Label.Text = "Time (s)";
        Plot.Axes.Left.Label.Text = "Acceleration (g)";

        var sus = type == SuspensionType.Front ? telemetryData.Front : telemetryData.Rear;
        if (!sus.Present || sus.Velocity is not { Length: > 0 }) return;

        var sampleRate = telemetryData.SampleRate;
        var period = 1.0 / sampleRate;
        var color = type == SuspensionType.Front ? FrontColor : RearColor;

        // Velocity-tuned WH (order 3, λ=11) leaves enough 30–93 Hz residue that a naive
        // 2nd differentiation produces unphysical g-peaks. Pre-smooth velocity with a
        // stronger WH (cutoff ≈29 Hz @ 860 SPS, just below mechanical bandwidth) before
        // the central difference. Acts only on the acceleration display; Velocity, Strokes
        // and histograms are unaffected. The smoother instance (and its ~50 MB factored
        // matrix) is shared between the front and rear acceleration plots.
        var accelSmoother = telemetryData.GetAccelSmoother();

        double[] ToAcceleration(double[] v)
        {
            var n = v.Length;
            var a = new double[n];
            if (n < 2) return a;
            var vs = accelSmoother.Smooth(v);
            a[0] = (vs[1] - vs[0]) * sampleRate / GravityMmPerS2;
            for (int i = 1; i < n - 1; i++)
                a[i] = (vs[i + 1] - vs[i - 1]) * sampleRate / 2.0 / GravityMmPerS2;
            a[n - 1] = (vs[n - 1] - vs[n - 2]) * sampleRate / GravityMmPerS2;
            return a;
        }

        static (double Max, double Min, double Rms) Stats(double[] a)
        {
            if (a.Length == 0) return (0, 0, 0);
            double mx = double.NegativeInfinity, mn = double.PositiveInfinity, sumSq = 0;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] > mx) mx = a[i];
                if (a[i] < mn) mn = a[i];
                sumSq += a[i] * a[i];
            }
            return (mx, mn, Math.Sqrt(sumSq / a.Length));
        }

        var acc = ToAcceleration(sus.Velocity);
        var sig = Plot.Add.Signal(acc, period);
        sig.Color = color;
        sig.LineWidth = 1;
        var duration = acc.Length * period;
        var stats = Stats(acc);

        double aMax = double.NegativeInfinity, aMin = double.PositiveInfinity;
        for (int i = 0; i < acc.Length; i++)
        {
            if (acc[i] > aMax) aMax = acc[i];
            if (acc[i] < aMin) aMin = acc[i];
        }
        if (double.IsInfinity(aMax)) { aMax = 1; aMin = -1; }

        // Optional zoom window: clamp the requested [start,end) to the data range, and fall
        // back to the full (unwindowed) view for a degenerate request — either too narrow to
        // be meaningful, or too narrow to cover a single sample once snapped to sample indices
        // — so the axes and stats never collapse. When a valid window applies, both the stats
        // readout (max/min/rms) and the separate aMax/aMin used for the Y-limits are
        // recomputed over just that sub-range so the zoom reveals local detail instead of the
        // whole-ride range.
        var winStart = 0.0;
        var winEnd = duration;
        var hasWindow = windowStartSeconds.HasValue && windowEndSeconds.HasValue;
        if (hasWindow)
        {
            winStart = Math.Clamp(windowStartSeconds!.Value, 0, duration);
            winEnd = Math.Clamp(windowEndSeconds!.Value, winStart, duration);
            if (winEnd - winStart < 1e-6)
            {
                hasWindow = false;
                winStart = 0.0;
                winEnd = duration;
            }
            else
            {
                var s0 = Math.Clamp((int)Math.Floor(winStart * sampleRate), 0, acc.Length);
                var s1 = Math.Clamp((int)Math.Ceiling(winEnd * sampleRate), 0, acc.Length);
                if (s1 <= s0)
                {
                    hasWindow = false;
                    winStart = 0.0;
                    winEnd = duration;
                }
                else
                {
                    stats = Stats(acc[s0..s1]);

                    aMax = double.NegativeInfinity;
                    aMin = double.PositiveInfinity;
                    for (int i = s0; i < s1; i++)
                    {
                        if (acc[i] > aMax) aMax = acc[i];
                        if (acc[i] < aMin) aMin = acc[i];
                    }
                    if (double.IsInfinity(aMax)) { aMax = 1; aMin = -1; }
                }
            }
        }

        var span = Math.Max(aMax - aMin, 1e-9);
        var top    = aMax + span * 0.05;
        var bottom = aMin - span * 0.05;
        Plot.Axes.SetLimitsY(bottom: bottom, top: top);

        if (hasWindow)
        {
            Plot.Axes.SetLimitsX(left: winStart, right: winEnd);

            static string FmtTime(double seconds)
            {
                var minutes = (int)(seconds / 60);
                var secs = seconds % 60;
                return $"{minutes}:{secs:00.0}";
            }

            SetTitle($"{prefix} acceleration over time — {FmtTime(winStart)}–{FmtTime(winEnd)}");
        }
        else if (duration > 0)
        {
            Plot.Axes.SetLimitsX(left: 0, right: duration);
        }

        Plot.Add.HorizontalLine(0, 1f, Color.FromHex("#dddddd"), LinePattern.Dotted);

        // Session-global airtime bands (no labels) for navigation context, matching the base travel
        // view. Prominent so brief jumps in long sessions stay visible; labels only in the zoom view.
        if (duration > 0)
            AddAirtimeOverlays(telemetryData.Airtimes, yTop: top, yBottom: bottom,
                maxDuration: duration, withLabels: false, fillAlpha: 0.45, minWidthPx: 3);

        // Legend — upper-left corner
        var legend = Plot.Add.Text(prefix, 0, top);
        legend.LabelFontColor = color;
        legend.LabelFontSize = 12;
        legend.LabelAlignment = Alignment.UpperLeft;
        legend.LabelOffsetX = 6;
        legend.LabelOffsetY = 6;

        // Stats readout (max/min/rms) — upper-right. PadLeft + NBSP keeps digits
        // right-aligned across rows; SVG normalization would otherwise collapse the
        // leading regular spaces. Matches VelocityTimeCroppedPlot's styling.
        var rightX = hasWindow ? winEnd : (duration > 0 ? duration : 1.0);
        static string N(double val) =>
            val.ToString("F3").PadLeft(7).Replace(' ', ' ');

        var text =
            $"max: {N(stats.Max)}\n" +
            $"min: {N(stats.Min)}\n" +
            $"rms: {N(stats.Rms)}";
        var statsLabel = Plot.Add.Text(text, rightX, top);
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
}
