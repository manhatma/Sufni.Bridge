using ScottPlot;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

/// <summary>
/// Time-history line chart of front and rear travel for the cropped session slice,
/// with a Front/Rear legend anchored in the lower-right corner.
/// </summary>
public class TravelTimeCroppedPlot(Plot plot) : TelemetryPlot(plot)
{
    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);

        SetTitle("Travel over time (cropped)");
        Plot.Layout.Fixed(new PixelPadding(55, 14, 50, 40));
        Plot.Axes.Bottom.Label.Text = "Time (s)";
        Plot.Axes.Left.Label.Text = "Travel (mm)";

        var sampleRate = telemetryData.SampleRate;
        var period = 1.0 / sampleRate;

        double maxDuration = 0;

        if (telemetryData.Front.Present && telemetryData.Front.Travel.Length > 0)
        {
            var sig = Plot.Add.Signal(telemetryData.Front.Travel, period);
            sig.Color = FrontColor;
            sig.LineWidth = 1;
            maxDuration = System.Math.Max(maxDuration, telemetryData.Front.Travel.Length * period);
        }

        if (telemetryData.Rear.Present && telemetryData.Rear.Travel.Length > 0)
        {
            var sig = Plot.Add.Signal(telemetryData.Rear.Travel, period);
            sig.Color = RearColor;
            sig.LineWidth = 1;
            maxDuration = System.Math.Max(maxDuration, telemetryData.Rear.Travel.Length * period);
        }

        // Y-axis: 0 at top (sag), actual max travel at bottom (inverted)
        double actualMax = 0;
        if (telemetryData.Front.Present)
            for (int i = 0; i < telemetryData.Front.Travel.Length; i++)
                if (telemetryData.Front.Travel[i] > actualMax) actualMax = telemetryData.Front.Travel[i];
        if (telemetryData.Rear.Present)
            for (int i = 0; i < telemetryData.Rear.Travel.Length; i++)
                if (telemetryData.Rear.Travel[i] > actualMax) actualMax = telemetryData.Rear.Travel[i];

        var bottom = actualMax > 0 ? actualMax * 1.05 : 1.0;
        var top    = 0.0;
        Plot.Axes.SetLimitsY(bottom: bottom, top: top);

        if (maxDuration > 0)
            Plot.Axes.SetLimitsX(left: 0, right: maxDuration);

        // Legend — lower-left corner. "bottom" is the largest Y value (inverted axis),
        // so place labels in that corner and stack them vertically.
        var range = bottom - top;
        var frontLegend = Plot.Add.Text("Front", 0, bottom - range * 0.08);
        frontLegend.LabelFontColor = FrontColor;
        frontLegend.LabelFontSize = 12;
        frontLegend.LabelAlignment = Alignment.LowerLeft;
        frontLegend.LabelOffsetX = 6;
        frontLegend.LabelOffsetY = -6;

        var rearLegend = Plot.Add.Text("Rear", 0, bottom);
        rearLegend.LabelFontColor = RearColor;
        rearLegend.LabelFontSize = 12;
        rearLegend.LabelAlignment = Alignment.LowerLeft;
        rearLegend.LabelOffsetX = 6;
        rearLegend.LabelOffsetY = -6;
    }
}
