using System;
using ScottPlot;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

/// <summary>
/// Time-history line chart of front and rear velocity for the cropped session slice,
/// with a Front/Rear legend anchored in the lower-right corner.
/// </summary>
public class VelocityTimeCroppedPlot(Plot plot) : TelemetryPlot(plot)
{
    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);

        SetTitle("Velocity over time");
        // Wider left padding — velocity tick labels are ~5 chars (e.g. "4.000"/"-4.000")
        // vs ~3 for travel, so the Y axis label ("Velocity (mm/s)") would otherwise be clipped.
        Plot.Layout.Fixed(new PixelPadding(72, 14, 50, 40));
        Plot.Axes.Bottom.Label.Text = "Time (s)";
        Plot.Axes.Left.Label.Text = "Velocity (mm/s)";

        var sampleRate = telemetryData.SampleRate;
        var period = 1.0 / sampleRate;

        double maxDuration = 0;
        double vMax = double.NegativeInfinity;
        double vMin = double.PositiveInfinity;

        void Track(double[] v)
        {
            for (int i = 0; i < v.Length; i++)
            {
                if (v[i] > vMax) vMax = v[i];
                if (v[i] < vMin) vMin = v[i];
            }
        }

        if (telemetryData.Front.Present && telemetryData.Front.Velocity is { Length: > 0 })
        {
            var sig = Plot.Add.Signal(telemetryData.Front.Velocity, period);
            sig.Color = FrontColor;
            sig.LineWidth = 1;
            maxDuration = Math.Max(maxDuration, telemetryData.Front.Velocity.Length * period);
            Track(telemetryData.Front.Velocity);
        }

        if (telemetryData.Rear.Present && telemetryData.Rear.Velocity is { Length: > 0 })
        {
            var sig = Plot.Add.Signal(telemetryData.Rear.Velocity, period);
            sig.Color = RearColor;
            sig.LineWidth = 1;
            maxDuration = Math.Max(maxDuration, telemetryData.Rear.Velocity.Length * period);
            Track(telemetryData.Rear.Velocity);
        }

        if (double.IsInfinity(vMax)) { vMax = 1; vMin = -1; }
        var span = Math.Max(vMax - vMin, 1e-9);
        var top    = vMax + span * 0.05;
        var bottom = vMin - span * 0.05;
        Plot.Axes.SetLimitsY(bottom: bottom, top: top);

        if (maxDuration > 0)
            Plot.Axes.SetLimitsX(left: 0, right: maxDuration);

        Plot.Add.HorizontalLine(0, 1f, Color.FromHex("#dddddd"), LinePattern.Dotted);

        // Legend — upper-left corner, stacked vertically
        var range = top - bottom;
        var frontLegend = Plot.Add.Text("Front", 0, top);
        frontLegend.LabelFontColor = FrontColor;
        frontLegend.LabelFontSize = 12;
        frontLegend.LabelAlignment = Alignment.UpperLeft;
        frontLegend.LabelOffsetX = 6;
        frontLegend.LabelOffsetY = 6;

        var rearLegend = Plot.Add.Text("Rear", 0, top - range * 0.08);
        rearLegend.LabelFontColor = RearColor;
        rearLegend.LabelFontSize = 12;
        rearLegend.LabelAlignment = Alignment.UpperLeft;
        rearLegend.LabelOffsetX = 6;
        rearLegend.LabelOffsetY = 6;
    }
}
