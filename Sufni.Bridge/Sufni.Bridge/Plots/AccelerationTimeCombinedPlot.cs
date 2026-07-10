using ScottPlot;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

/// <summary>
/// Front and rear acceleration (g) over time overlaid in a single plot. Used for the zoomed Misc
/// view, replacing the two separate acceleration-over-time plots. Acceleration is derived exactly
/// as AccelerationTimeCroppedPlot does (shared stronger WH pre-smoother + central difference).
/// </summary>
public class AccelerationTimeCombinedPlot(Plot plot, double? windowStartSeconds = null, double? windowEndSeconds = null)
    : TelemetryPlot(plot)
{
    private const double GravityMmPerS2 = 9806.65;

    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);

        SetTitle("Acceleration over time");
        Plot.Layout.Fixed(new PixelPadding(55, 14, 50, 40));
        Plot.Axes.Bottom.Label.Text = "Time (s)";
        Plot.Axes.Left.Label.Text = "Acceleration (g)";

        var sampleRate = telemetryData.SampleRate;
        var period = 1.0 / sampleRate;
        var accelSmoother = telemetryData.GetAccelSmoother();

        double[]? ToAcceleration(Suspension side)
        {
            if (!side.Present || side.Velocity is not { Length: > 0 })
                return null;
            var v = side.Velocity;
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

        var front = ToAcceleration(telemetryData.Front);
        var rear = ToAcceleration(telemetryData.Rear);
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

        double aMax = double.NegativeInfinity, aMin = double.PositiveInfinity;
        ScanMinMax(front, s0, s1, ref aMax, ref aMin);
        ScanMinMax(rear, s0, s1, ref aMax, ref aMin);
        if (double.IsInfinity(aMax)) { aMax = 1; aMin = -1; }

        var span = System.Math.Max(aMax - aMin, 1e-9);
        var top = aMax + span * 0.05;
        var bottom = aMin - span * 0.05;
        Plot.Axes.SetLimitsY(bottom: bottom, top: top);

        if (hasWindow)
        {
            Plot.Axes.SetLimitsX(left: winStart, right: winEnd);
            SetTitle($"Acceleration over time — {FormatWindowTime(winStart)}–{FormatWindowTime(winEnd)}");
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
            "g", hasWindow ? winEnd : maxDuration, top);
    }
}
