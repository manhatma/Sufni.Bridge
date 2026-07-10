using System;
using ScottPlot;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

public class TravelTimeCroppedPlot(Plot plot, SuspensionType type,
    double? windowStartSeconds = null, double? windowEndSeconds = null) : TelemetryPlot(plot)
{
    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);

        var side = type == SuspensionType.Front ? telemetryData.Front : telemetryData.Rear;
        var sidePrefix = type == SuspensionType.Front ? "Front" : "Rear";
        // "(cropped)" is intentionally omitted from these titles (isCropped kept for call-site
        // compatibility); the crop state is conveyed elsewhere in the UI.
        SetTitle($"{sidePrefix} travel over time");
        Plot.Layout.Fixed(new PixelPadding(55, 14, 50, 40));
        Plot.Axes.Bottom.Label.Text = "Time (s)";
        Plot.Axes.Left.Label.Text = "Travel (mm)";

        if (!side.Present || side.Travel.Length == 0)
            return;

        var sampleRate = telemetryData.SampleRate;
        var period = 1.0 / sampleRate;
        var color = type == SuspensionType.Front ? FrontColor : RearColor;

        // Display the WH-smoothed travel (same parameters as the velocity pipeline) so the
        // curve matches what differentiation actually sees. The raw, LSB-quantised signal
        // remains visible in the crop dialog (TravelTimeHistoryPlot) for sensor diagnostics.
        // Shared memo: the pitch computation uses the same smoothed series.
        var smoothed = telemetryData.GetSmoothedTravel(type);

        var sig = Plot.Add.Signal(smoothed, period);
        sig.Color = color;
        sig.LineWidth = 1;
        var maxDuration = smoothed.Length * period;

        // Optional zoom window: clamp the requested [start,end) to the data range, and fall
        // back to the full (unwindowed) view for a degenerate request — either too narrow to
        // be meaningful, or too narrow to cover a single sample once snapped to sample indices
        // — so the axes and stats never collapse. When a valid window applies, actualMax (and
        // therefore the Y-limits) are recomputed over just that sub-range so the zoom reveals
        // local detail instead of the whole-ride range.
        var winStart = 0.0;
        var winEnd = maxDuration;
        var hasWindow = windowStartSeconds.HasValue && windowEndSeconds.HasValue;
        var s0 = 0;
        var s1 = smoothed.Length;
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
                var ws0 = Math.Clamp((int)Math.Floor(winStart * sampleRate), 0, smoothed.Length);
                var ws1 = Math.Clamp((int)Math.Ceiling(winEnd * sampleRate), 0, smoothed.Length);
                if (ws1 <= ws0)
                {
                    hasWindow = false;
                    winStart = 0.0;
                    winEnd = maxDuration;
                }
                else
                {
                    s0 = ws0;
                    s1 = ws1;
                }
            }
        }

        double actualMax = 0;
        for (int i = s0; i < s1; i++)
            if (smoothed[i] > actualMax) actualMax = smoothed[i];

        var bottom = actualMax > 0 ? actualMax * 1.05 : 1.0;
        var top    = 0.0;
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

            SetTitle($"{sidePrefix} travel over time — {FmtTime(winStart)}–{FmtTime(winEnd)}");
        }
        else
        {
            Plot.Axes.SetLimitsX(left: 0, right: maxDuration);
        }

        // Airtimes are session-global (front and rear share the same jumps), so both the front
        // and rear plot draw the same overlay.
        if (actualMax > 0)
        {
            var overlayDuration = hasWindow ? winEnd - winStart : maxDuration;
            // Base (unzoomed) view: prominent bands, NO labels — labels are shown only in the zoomed
            // combined view (in long sessions they otherwise overlap into an unreadable cluster).
            AddAirtimeOverlays(telemetryData.Airtimes, yTop: top, yBottom: bottom,
                maxDuration: overlayDuration, withLabels: false, fillAlpha: 0.45, minWidthPx: 3);
        }
    }
}
