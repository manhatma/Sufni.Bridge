using ScottPlot;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

/// <summary>
/// Full-session front+rear velocity (m/s) overview. Used as the Damper page's zoom mini-map, with
/// optional (prominent) airtime bands for navigation. No legend — it is a compact context strip.
/// </summary>
public class VelocityTimeHistoryPlot(Plot plot, bool showAirtimeBands = false) : TelemetryPlot(plot)
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

        if (hasFront) { var s = Plot.Add.Signal(front!, period); s.Color = FrontColor; s.LineWidth = 1; }
        if (hasRear)  { var s = Plot.Add.Signal(rear!, period);  s.Color = RearColor;  s.LineWidth = 1; }

        double vMax = double.NegativeInfinity, vMin = double.PositiveInfinity;
        ScanMinMax(front, 0, length, ref vMax, ref vMin);
        ScanMinMax(rear, 0, length, ref vMax, ref vMin);
        if (double.IsInfinity(vMax)) { vMax = 1; vMin = -1; }

        var span = System.Math.Max(vMax - vMin, 1e-9);
        var top = vMax + span * 0.05;
        var bottom = vMin - span * 0.05;
        Plot.Axes.SetLimitsY(bottom: bottom, top: top);
        if (maxDuration > 0)
            Plot.Axes.SetLimitsX(left: 0, right: maxDuration);

        Plot.Add.HorizontalLine(0, 1f, Color.FromHex("#dddddd"), LinePattern.Dotted);

        if (showAirtimeBands && maxDuration > 0)
            AddAirtimeOverlays(telemetryData.Airtimes, yTop: top, yBottom: bottom,
                maxDuration: maxDuration, withLabels: false, fillAlpha: 0.45, minWidthPx: 3);
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
