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

        var sig = Plot.Add.Signal(side.Travel, period);
        sig.Color = color;
        sig.LineWidth = 1;
        var maxDuration = side.Travel.Length * period;

        double actualMax = 0;
        for (int i = 0; i < side.Travel.Length; i++)
            if (side.Travel[i] > actualMax) actualMax = side.Travel[i];

        var bottom = actualMax > 0 ? actualMax * 1.05 : 1.0;
        var top    = 0.0;
        Plot.Axes.SetLimitsY(bottom: bottom, top: top);
        Plot.Axes.SetLimitsX(left: 0, right: maxDuration);
    }
}
