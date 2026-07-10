using ScottPlot;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

/// <summary>
/// Front and rear velocity over time overlaid in a single plot. Used for the zoomed Damper view,
/// where the two separate velocity-over-time plots are replaced by this combined one. Optional
/// time-window handling matches VelocityTimeCroppedPlot (X-limits + window-local Y).
/// </summary>
public class VelocityTimeCombinedPlot(Plot plot, double? windowStartSeconds = null, double? windowEndSeconds = null)
    : TelemetryPlot(plot)
{
    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);

        SetTitle("Velocity over time");
        Plot.Layout.Fixed(new PixelPadding(55, 14, 50, 40));
        Plot.Axes.Bottom.Label.Text = "Time (s)";
        Plot.Axes.Left.Label.Text = "Velocity (m/s)";

        var sampleRate = telemetryData.SampleRate;
        var period = 1.0 / sampleRate;

        var front = ToMetersPerSecond(telemetryData.Front);
        var rear = ToMetersPerSecond(telemetryData.Rear);
        var hasFront = front is { Length: > 0 };
        var hasRear = rear is { Length: > 0 };
        if (!hasFront && !hasRear)
            return;

        var length = System.Math.Max(hasFront ? front!.Length : 0, hasRear ? rear!.Length : 0);
        var maxDuration = length * period;

        if (hasFront)
        {
            var s = Plot.Add.Signal(front!, period);
            s.Color = FrontColor;
            s.LineWidth = 1;
        }
        if (hasRear)
        {
            var s = Plot.Add.Signal(rear!, period);
            s.Color = RearColor;
            s.LineWidth = 1;
        }

        var (winStart, winEnd, s0, s1, hasWindow) =
            ResolveTimeWindow(windowStartSeconds, windowEndSeconds, maxDuration, sampleRate, length);

        double vMax = double.NegativeInfinity, vMin = double.PositiveInfinity;
        ScanMinMax(front, s0, s1, ref vMax, ref vMin);
        ScanMinMax(rear, s0, s1, ref vMax, ref vMin);
        if (double.IsInfinity(vMax)) { vMax = 1; vMin = -1; }

        var span = System.Math.Max(vMax - vMin, 1e-9);
        var top = vMax + span * 0.05;
        var bottom = vMin - span * 0.05;
        Plot.Axes.SetLimitsY(bottom: bottom, top: top);

        if (hasWindow)
        {
            Plot.Axes.SetLimitsX(left: winStart, right: winEnd);
            SetTitle($"Velocity over time — {FormatWindowTime(winStart)}–{FormatWindowTime(winEnd)}");
        }
        else
        {
            Plot.Axes.SetLimitsX(left: 0, right: maxDuration);
        }

        Plot.Add.HorizontalLine(0, 1f, Color.FromHex("#dddddd"), LinePattern.Dotted);
        AddCombinedLegend(hasFront, hasRear, hasWindow ? winStart : 0, top);

        // Session-global airtime bands + duration labels (this is the zoomed detail view).
        var overlayDuration = hasWindow ? winEnd - winStart : maxDuration;
        AddAirtimeOverlays(telemetryData.Airtimes, yTop: top, yBottom: bottom, maxDuration: overlayDuration);

        // Compact single-colour stats box, front/rear columns, over the shown window.
        AddCombinedStatsBox(ComputeSideStats(front, s0, s1), ComputeSideStats(rear, s0, s1),
            "m/s", hasWindow ? winEnd : maxDuration, top);
    }

    private static double[]? ToMetersPerSecond(Suspension side)
    {
        if (!side.Present || side.Velocity is not { Length: > 0 })
            return null;
        var v = new double[side.Velocity.Length];
        for (int i = 0; i < v.Length; i++)
            v[i] = side.Velocity[i] / 1000.0;
        return v;
    }
}
