using System;
using ScottPlot;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

/// <summary>
/// Front and rear (WH-smoothed) travel over time overlaid in a single plot. Used for the zoomed
/// Spring view, where the two separate travel-over-time plots are replaced by this combined one.
/// Mirrors TravelTimeCroppedPlot's optional time-window handling (X-limits + window-local Y and
/// airtime overlay), but draws both wheels at once.
/// </summary>
public class TravelTimeCombinedPlot(Plot plot, double? windowStartSeconds = null, double? windowEndSeconds = null)
    : TelemetryPlot(plot)
{
    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);

        SetTitle("Travel over time");
        Plot.Layout.Fixed(new PixelPadding(55, 14, 50, 40));
        Plot.Axes.Bottom.Label.Text = "Time (s)";
        Plot.Axes.Left.Label.Text = "Travel (mm)";

        var sampleRate = telemetryData.SampleRate;
        var period = 1.0 / sampleRate;

        var front = telemetryData.Front.Present ? telemetryData.GetSmoothedTravel(SuspensionType.Front) : null;
        var rear = telemetryData.Rear.Present ? telemetryData.GetSmoothedTravel(SuspensionType.Rear) : null;
        var hasFront = front is { Length: > 0 };
        var hasRear = rear is { Length: > 0 };
        if (!hasFront && !hasRear)
            return;

        var length = Math.Max(hasFront ? front!.Length : 0, hasRear ? rear!.Length : 0);
        var maxDuration = length * period;

        if (hasFront)
        {
            var sig = Plot.Add.Signal(front!, period);
            sig.Color = FrontColor;
            sig.LineWidth = 1;
        }

        if (hasRear)
        {
            var sig = Plot.Add.Signal(rear!, period);
            sig.Color = RearColor;
            sig.LineWidth = 1;
        }

        // Optional zoom window: clamp to the data and fall back to the full view for a degenerate
        // request (same guards as TravelTimeCroppedPlot). When a valid window applies, the Y-limit
        // is recomputed over just that sub-range so the zoom reveals local detail.
        var winStart = 0.0;
        var winEnd = maxDuration;
        var hasWindow = windowStartSeconds.HasValue && windowEndSeconds.HasValue;
        var s0 = 0;
        var s1 = length;
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
                var ws0 = Math.Clamp((int)Math.Floor(winStart * sampleRate), 0, length);
                var ws1 = Math.Clamp((int)Math.Ceiling(winEnd * sampleRate), 0, length);
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
        void Scan(double[]? a)
        {
            if (a is null) return;
            var hi = Math.Min(s1, a.Length);
            for (int i = Math.Min(s0, a.Length); i < hi; i++)
                if (a[i] > actualMax) actualMax = a[i];
        }
        Scan(front);
        Scan(rear);

        var bottom = actualMax > 0 ? actualMax * 1.05 : 1.0;
        const double top = 0.0;
        Plot.Axes.SetLimitsY(bottom: bottom, top: top);

        if (hasWindow)
        {
            Plot.Axes.SetLimitsX(left: winStart, right: winEnd);
            SetTitle($"Travel over time — {FmtTime(winStart)}–{FmtTime(winEnd)}");
        }
        else
        {
            Plot.Axes.SetLimitsX(left: 0, right: maxDuration);
        }

        // Compact colour key (Front blue / Rear teal), upper-left, so the two overlaid lines read.
        var legendX = hasWindow ? winStart : 0;
        if (hasFront)
        {
            var l = Plot.Add.Text("Front", legendX, top);
            l.LabelFontColor = FrontColor;
            l.LabelFontSize = 11;
            l.LabelBold = true;
            l.LabelAlignment = Alignment.UpperLeft;
            l.LabelOffsetX = 6;
            l.LabelOffsetY = 6;
        }
        if (hasRear)
        {
            var l = Plot.Add.Text("Rear", legendX, top);
            l.LabelFontColor = RearColor;
            l.LabelFontSize = 11;
            l.LabelBold = true;
            l.LabelAlignment = Alignment.UpperLeft;
            l.LabelOffsetX = 6;
            l.LabelOffsetY = 22;
        }

        // Airtimes are session-global — draw them (with duration labels) on the combined plot too.
        if (actualMax > 0)
        {
            var overlayDuration = hasWindow ? winEnd - winStart : maxDuration;
            AddAirtimeOverlays(telemetryData.Airtimes, yTop: top, yBottom: bottom, maxDuration: overlayDuration);
        }

        static string FmtTime(double seconds)
        {
            var minutes = (int)(seconds / 60);
            var secs = seconds % 60;
            return $"{minutes}:{secs:00.0}";
        }
    }
}
