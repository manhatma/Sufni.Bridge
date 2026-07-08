using ScottPlot;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

public class TravelTimeCroppedPlot(Plot plot, SuspensionType type, bool isCropped) : TelemetryPlot(plot)
{
    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);

        var side = type == SuspensionType.Front ? telemetryData.Front : telemetryData.Rear;
        var sidePrefix = type == SuspensionType.Front ? "Front" : "Rear";
        var suffix = isCropped ? " (cropped)" : "";
        SetTitle($"{sidePrefix} travel over time{suffix}");
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

        double actualMax = 0;
        for (int i = 0; i < smoothed.Length; i++)
            if (smoothed[i] > actualMax) actualMax = smoothed[i];

        var bottom = actualMax > 0 ? actualMax * 1.05 : 1.0;
        var top    = 0.0;
        Plot.Axes.SetLimitsY(bottom: bottom, top: top);
        Plot.Axes.SetLimitsX(left: 0, right: maxDuration);

        // Airtimes are session-global (front and rear share the same jumps), so both the front
        // and rear plot draw the same overlay.
        if (actualMax > 0)
        {
            AddAirtimeOverlays(telemetryData.Airtimes, yTop: top, yBottom: bottom, maxDuration: maxDuration);
        }
    }
}
