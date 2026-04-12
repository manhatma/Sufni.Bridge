using ScottPlot;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

/// <summary>
/// Time-history line chart showing raw front and rear travel over the full session duration.
/// Always uses the full (uncompressed) TelemetryData — crop overlays are handled in the UI.
/// </summary>
public class TravelTimeHistoryPlot(Plot plot) : TelemetryPlot(plot)
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

        // Y-axis: 0 at top (sag), max travel at bottom — inverted so "more compression = down"
        double maxTravel = 0;
        if (telemetryData.Front.Present) maxTravel = System.Math.Max(maxTravel, telemetryData.Linkage.MaxFrontTravel);
        if (telemetryData.Rear.Present)  maxTravel = System.Math.Max(maxTravel, telemetryData.Linkage.MaxRearTravel);
        if (maxTravel > 0)
            Plot.Axes.SetLimitsY(bottom: maxTravel * 1.05, top: -maxTravel * 0.05);

        // X-axis always starts at 0
        if (maxDuration > 0)
            Plot.Axes.SetLimitsX(left: 0, right: maxDuration);
    }
}
