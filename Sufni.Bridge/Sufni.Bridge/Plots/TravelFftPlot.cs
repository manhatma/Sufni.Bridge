using System;
using System.Collections.Generic;
using System.Numerics;
using MathNet.Numerics;
using MathNet.Numerics.IntegralTransforms;
using ScottPlot;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

/// <summary>
/// Welch-method amplitude (magnitude) spectrum of front/rear travel.
/// Hanning window, 50% overlap, segment length 4096 (Δf ≈ 0.21 Hz @ 860 Hz).
/// X axis: 0–30 Hz (focus 0.5–5 Hz for body natural frequencies);
/// Y axis: amplitude (mm).
/// </summary>
public class TravelFftPlot(Plot plot, SuspensionType type) : TelemetryPlot(plot)
{
    private const int SegmentLength = 4096;
    private const double MaxFrequencyHz = 30.0;
    private const int MinSegmentLength = 64;

    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);

        var side = type == SuspensionType.Front ? telemetryData.Front : telemetryData.Rear;
        var prefix = type == SuspensionType.Front ? "Front" : "Rear";
        SetTitle($"{prefix} travel FFT");
        Plot.Layout.Fixed(new PixelPadding(55, 14, 50, 40));
        Plot.Axes.Bottom.Label.Text = "Frequency (Hz)";
        Plot.Axes.Left.Label.Text = "Amplitude (mm)";

        if (!side.Present || side.Travel.Length < MinSegmentLength) return;

        var travel = side.Travel;
        var n = travel.Length;
        var sampleRate = telemetryData.SampleRate;

        // Fixed 4096-point segments → Δf = 860/4096 ≈ 0.21 Hz at 860 Hz sample rate.
        // Fall back to the largest power-of-two ≤ n if the signal is shorter.
        int segLen = SegmentLength;
        while (segLen > n) segLen /= 2;
        if (segLen < MinSegmentLength) return;

        var window = Window.Hann(segLen);
        double winSum = 0;
        for (int i = 0; i < segLen; i++) winSum += window[i];

        int step = segLen / 2; // 50% overlap
        int bins = segLen / 2;
        var avg = new double[bins];
        int segCount = 0;
        var buffer = new Complex[segLen];

        for (int start = 0; start + segLen <= n; start += step)
        {
            double mean = 0;
            for (int i = 0; i < segLen; i++) mean += travel[start + i];
            mean /= segLen;

            for (int i = 0; i < segLen; i++)
                buffer[i] = new Complex((travel[start + i] - mean) * window[i], 0);

            Fourier.Forward(buffer, FourierOptions.NoScaling);

            for (int k = 0; k < bins; k++)
                avg[k] += buffer[k].Magnitude;

            segCount++;
        }

        if (segCount == 0) return;

        // Single-sided amplitude scaling: 2 / sum(window). For a sinusoid A·sin(2πf₀t)
        // this yields A at the matching bin.
        double scale = 2.0 / winSum;
        double dF = (double)sampleRate / segLen;
        double xMax = Math.Min(sampleRate / 2.0, MaxFrequencyHz);

        // Skip DC (k=0): windowing leaves residual DC content that would dominate
        // the y axis but carries no useful info.
        int firstBin = 1;
        int lastBin = Math.Min(bins - 1, (int)Math.Ceiling(xMax / dF));
        if (lastBin < firstBin) return;

        int len = lastBin - firstBin + 1;
        var color = type == SuspensionType.Front ? FrontColor : RearColor;

        double yMax = 0;
        var bars = new List<Bar>(len);
        double barSize = dF * 0.9;
        for (int i = 0; i < len; i++)
        {
            int k = firstBin + i;
            double amplitude = (avg[k] / segCount) * scale;
            if (amplitude > yMax) yMax = amplitude;
            bars.Add(new Bar
            {
                Position = k * dF,
                Value = amplitude,
                ValueBase = 0,
                Size = barSize,
                FillColor = color.WithOpacity(0.4),
                LineColor = color,
                LineWidth = 1f,
                Orientation = Orientation.Vertical,
            });
        }

        if (yMax <= 0) yMax = 1.0;

        Plot.Add.Bars(bars);
        Plot.Axes.SetLimits(left: 0, right: xMax, bottom: 0, top: yMax * 1.05);
        Plot.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic();
    }
}
