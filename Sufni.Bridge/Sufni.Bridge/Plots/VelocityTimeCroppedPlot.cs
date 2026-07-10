using System;
using ScottPlot;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

public class VelocityTimeCroppedPlot(Plot plot, SuspensionType type,
    double? windowStartSeconds = null, double? windowEndSeconds = null) : TelemetryPlot(plot)
{
    private static readonly Color StatColor = Color.FromHex("#FFD700");

    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);

        var side = type == SuspensionType.Front ? telemetryData.Front : telemetryData.Rear;
        var baseTitle = type == SuspensionType.Front
            ? "Front velocity over time"
            : "Rear velocity over time";
        SetTitle(baseTitle);
        Plot.Layout.Fixed(new PixelPadding(55, 14, 50, 40));
        Plot.Axes.Bottom.Label.Text = "Time (s)";
        Plot.Axes.Left.Label.Text = "Velocity (m/s)";

        if (!side.Present || side.Velocity is not { Length: > 0 })
            return;

        var sampleRate = telemetryData.SampleRate;
        var period = 1.0 / sampleRate;
        var color = type == SuspensionType.Front ? FrontColor : RearColor;

        var v = new double[side.Velocity.Length];
        double vMax = double.NegativeInfinity;
        double vMin = double.PositiveInfinity;
        double sumSq = 0;
        for (int i = 0; i < v.Length; i++)
        {
            v[i] = side.Velocity[i] / 1000.0;
            if (v[i] > vMax) vMax = v[i];
            if (v[i] < vMin) vMin = v[i];
            sumSq += v[i] * v[i];
        }
        var rms = Math.Sqrt(sumSq / v.Length);

        var sig = Plot.Add.Signal(v, period);
        sig.Color = color;
        sig.LineWidth = 1;
        var maxDuration = v.Length * period;

        // Optional zoom window: clamp the requested [start,end) to the data range, and fall
        // back to the full (unwindowed) view for a degenerate request — either too narrow to
        // be meaningful, or too narrow to cover a single sample once snapped to sample indices
        // — so the axes and stats never collapse. When a valid window applies, vMax/vMin/rms
        // are recomputed over just that sub-range so the zoom (and its stats readout) reveals
        // local detail instead of the whole-ride range.
        var winStart = 0.0;
        var winEnd = maxDuration;
        var hasWindow = windowStartSeconds.HasValue && windowEndSeconds.HasValue;
        if (hasWindow)
        {
            winStart = Math.Clamp(windowStartSeconds!.Value, 0, maxDuration);
            winEnd = Math.Clamp(windowEndSeconds!.Value, winStart, maxDuration);
            if (winEnd - winStart < 1e-6)
            {
                hasWindow = false;
                winStart = 0.0;
                winEnd = maxDuration;
            }
            else
            {
                var s0 = Math.Clamp((int)Math.Floor(winStart * sampleRate), 0, v.Length);
                var s1 = Math.Clamp((int)Math.Ceiling(winEnd * sampleRate), 0, v.Length);
                if (s1 <= s0)
                {
                    hasWindow = false;
                    winStart = 0.0;
                    winEnd = maxDuration;
                }
                else
                {
                    vMax = double.NegativeInfinity;
                    vMin = double.PositiveInfinity;
                    sumSq = 0;
                    for (int i = s0; i < s1; i++)
                    {
                        if (v[i] > vMax) vMax = v[i];
                        if (v[i] < vMin) vMin = v[i];
                        sumSq += v[i] * v[i];
                    }
                    rms = Math.Sqrt(sumSq / (s1 - s0));
                }
            }
        }

        var span = Math.Max(vMax - vMin, 1e-9);
        var top    = vMax + span * 0.05;
        var bottom = vMin - span * 0.05;
        Plot.Axes.SetLimitsY(bottom: bottom, top: top);

        if (hasWindow)
            Plot.Axes.SetLimitsX(left: winStart, right: winEnd);
        else
            Plot.Axes.SetLimitsX(left: 0, right: maxDuration);

        Plot.Add.HorizontalLine(0, 1f, Color.FromHex("#dddddd"), LinePattern.Dotted);

        // Session-global airtime bands (no labels) for navigation context, matching the base travel
        // view. Prominent so brief jumps in long sessions stay visible; labels only in the zoom view.
        if (maxDuration > 0)
            AddAirtimeOverlays(telemetryData.Airtimes, yTop: top, yBottom: bottom,
                maxDuration: maxDuration, withLabels: false, fillAlpha: 0.45, minWidthPx: 3);

        static string N(double val) =>
            val.ToString("F3").PadLeft(7).Replace(' ', ' ');

        var statsText =
            $"max: {N(vMax)}\n" +
            $"min: {N(vMin)}\n" +
            $"rms: {N(rms)}";

        var labelX = hasWindow ? winEnd : maxDuration;
        var statsLabel = Plot.Add.Text(statsText, labelX, top);
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

        if (hasWindow)
        {
            static string FmtTime(double seconds)
            {
                var minutes = (int)(seconds / 60);
                var secs = seconds % 60;
                return $"{minutes}:{secs:00.0}";
            }

            SetTitle($"{baseTitle} — {FmtTime(winStart)}–{FmtTime(winEnd)}");
        }
    }
}
