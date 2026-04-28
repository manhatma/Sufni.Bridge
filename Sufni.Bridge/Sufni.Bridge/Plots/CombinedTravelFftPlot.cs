using System;
using System.Linq;
using ScottPlot;
using ScottPlot.TickGenerators;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

public class CombinedTravelFftPlot(Plot plot) : TelemetryPlot(plot)
{
    private const int SegmentLength = 4096;
    private const double MaxFrequencyHz = 30.0;
    private const double PeakSearchMinHz = 1.0;
    private const double PeakSearchMaxHz = 5.0;

    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);

        SetTitle("Wheel travel FFT (Front vs Rear)");
        Plot.Layout.Fixed(new PixelPadding(55, 14, 50, 40));
        Plot.Axes.Bottom.Label.Text = "Frequency (Hz)";
        Plot.Axes.Left.Label.Text = "Amplitude (mm)";

        var sampleRate = telemetryData.SampleRate;
        double yMax = 0;
        var drewAny = false;

        if (telemetryData.Front.Present && telemetryData.Front.Travel is { Length: >= 64 })
        {
            yMax = Math.Max(yMax, AddSpectrum(telemetryData.Front.Travel, sampleRate, FrontColor, "Front"));
            drewAny = true;
        }
        if (telemetryData.Rear.Present && telemetryData.Rear.Travel is { Length: >= 64 })
        {
            yMax = Math.Max(yMax, AddSpectrum(telemetryData.Rear.Travel, sampleRate, RearColor, "Rear"));
            drewAny = true;
        }

        if (!drewAny) return;

        if (yMax <= 0) yMax = 1;
        Plot.Axes.SetLimits(left: 0, right: MaxFrequencyHz, bottom: 0, top: yMax * 1.15);
        Plot.Axes.Bottom.TickGenerator = new NumericFixedInterval(5);
    }

    private double AddSpectrum(double[] signal, int sampleRate, Color color, string name)
    {
        var spec = TelemetryData.ComputeWelchSpectrum(signal, sampleRate, SegmentLength);
        if (spec.Frequencies.Length == 0) return 0;

        // Restrict to display range, skip DC bin (k=0).
        var idxMax = spec.Frequencies.Length - 1;
        for (var i = 1; i < spec.Frequencies.Length; i++)
        {
            if (spec.Frequencies[i] > MaxFrequencyHz) { idxMax = i; break; }
        }

        var xs = spec.Frequencies.Skip(1).Take(idxMax - 1).ToArray();
        var ys = spec.Amplitudes.Skip(1).Take(idxMax - 1).ToArray();
        if (xs.Length == 0) return 0;

        var line = Plot.Add.Scatter(xs, ys);
        line.Color = color;
        line.MarkerStyle.IsVisible = false;
        line.LineStyle.Width = 2;
        line.LineStyle.Pattern = LinePattern.Solid;

        var (peakF, peakA) = TelemetryData.FindDominantPeak(spec, PeakSearchMinHz, PeakSearchMaxHz);
        if (!double.IsNaN(peakF))
        {
            var marker = Plot.Add.Marker(peakF, peakA);
            marker.MarkerShape = MarkerShape.OpenCircle;
            marker.MarkerSize = 8;
            marker.MarkerLineWidth = 2;
            marker.Color = color;

            var label = Plot.Add.Text($"{name}: {peakF:0.00} Hz", peakF, peakA);
            label.LabelFontColor = color;
            label.LabelFontSize = 11;
            label.LabelAlignment = Alignment.LowerLeft;
            label.LabelOffsetX = 6;
            label.LabelOffsetY = -4;
        }

        return ys.Max();
    }
}
