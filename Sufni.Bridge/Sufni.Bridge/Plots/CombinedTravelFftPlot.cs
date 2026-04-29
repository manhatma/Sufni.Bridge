using System;
using System.Collections.Generic;
using ScottPlot;
using ScottPlot.TickGenerators;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

/// <summary>
/// Overlaid Welch-method spectrum of Front and Rear wheel travel.
/// Log-log axes: X = log10(frequency Hz), Y = amplitude in dB referenced to 0.01 mm.
/// ScottPlot 5 has no native log scale; we transform the data and use a NumericManual
/// tick generator to render decade labels back in Hz on the X axis.
///
/// Constructor parameters allow rendering different frequency windows from the same
/// underlying Welch spectrum (low/full range with body-peak markers, high range
/// without markers, etc.). Peak search is disabled when peakMaxHz <= peakMinHz.
/// </summary>
public class CombinedTravelFftPlot(
    Plot plot,
    double minHz = 0.1,
    double maxHz = 100.0,
    double peakMinHz = 0.5,
    double peakMaxHz = 5.0,
    int segmentLength = 8192,
    bool fitYAxisToData = false,
    double topHeadroomDb = 8.0)
    : TelemetryPlot(plot)
{
    private const double ReferenceAmplitudeMm = 0.01;
    // Wider dynamic range than the original ±20 dB so secondary peaks (tire/mass
    // resonances, harmonics) sit visibly above the floor instead of being crushed
    // against it.
    private const double FloorDb = -40.0;
    // Fixed Y window when fitYAxisToData = false: bottom at 0 dB, top at MinTopDb +
    // topHeadroomDb. If a peak exceeds MinTopDb the upper bound grows so the marker
    // stays inside the plot.
    private const double AxisBottomDb = 0.0;
    private const double MinTopDb = 60.0;

    private bool PeakMarkersEnabled => peakMaxHz > peakMinHz;

    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);

        var df = telemetryData.SampleRate > 0
            ? (double)telemetryData.SampleRate / segmentLength
            : 0.0;
        SetTitle($"Wheel travel FFT — Δf ≈ {df:0.00} Hz");
        Plot.Layout.Fixed(new PixelPadding(55, 14, 50, 40));
        Plot.Axes.Bottom.Label.Text = "Frequency (Hz)";
        Plot.Axes.Left.Label.Text = "Amplitude (dB re 0.01 mm)";

        // SufniPlot hides minor ticks globally; for log X they're the visual cue
        // that the spacing between decades is logarithmic. Re-enable them on
        // the bottom axis only.
        Plot.Axes.Bottom.MinorTickStyle.Length = 4;
        Plot.Axes.Bottom.MinorTickStyle.Width = 1;
        Plot.Axes.Bottom.MinorTickStyle.Color = Color.FromHex("#505558");

        var sampleRate = telemetryData.SampleRate;
        double yMaxDb = FloorDb;
        double yMinDb = double.PositiveInfinity;
        var drewAny = false;

        if (telemetryData.Front.Present && telemetryData.Front.Travel is { Length: >= 64 })
        {
            var (lo, hi) = AddSpectrum(telemetryData.Front.Travel, sampleRate, FrontColor);
            yMaxDb = Math.Max(yMaxDb, hi);
            yMinDb = Math.Min(yMinDb, lo);
            drewAny = true;
        }
        if (telemetryData.Rear.Present && telemetryData.Rear.Travel is { Length: >= 64 })
        {
            var (lo, hi) = AddSpectrum(telemetryData.Rear.Travel, sampleRate, RearColor);
            yMaxDb = Math.Max(yMaxDb, hi);
            yMinDb = Math.Min(yMinDb, lo);
            drewAny = true;
        }

        if (!drewAny) return;

        var xLeft = Math.Log10(minHz);
        var xRight = Math.Log10(maxHz);

        double yBottom, yTop;
        if (fitYAxisToData)
        {
            // Snap to nearest 5-dB grid so ticks line up with the data range.
            yBottom = Math.Floor(yMinDb / 5.0) * 5.0;
            yTop = yMaxDb + topHeadroomDb;
            if (yTop - yBottom < 20) yTop = yBottom + 20;
        }
        else
        {
            yBottom = AxisBottomDb;
            yTop = Math.Max(yMaxDb, MinTopDb) + topHeadroomDb;
        }
        Plot.Axes.SetLimits(left: xLeft, right: xRight, bottom: yBottom, top: yTop);

        Plot.Axes.Bottom.TickGenerator = BuildLogTickGenerator(minHz, maxHz);
        Plot.Axes.Left.TickGenerator = new NumericFixedInterval(10);

        // Legend in the upper-right corner — same pattern as BalancePlot.
        AddCornerLegend(xRight, yTop);
    }

    /// <returns>(yMinDb, yMaxDb) of the data drawn within the visible X range.</returns>
    private (double, double) AddSpectrum(double[] signal, int sampleRate, Color color)
    {
        var spec = TelemetryData.ComputeWelchSpectrum(signal, sampleRate, segmentLength);
        if (spec.Frequencies.Length == 0) return (FloorDb, FloorDb);

        var xs = new List<double>(spec.Frequencies.Length);
        var ys = new List<double>(spec.Frequencies.Length);
        double yMax = FloorDb;
        double yMin = double.PositiveInfinity;

        // Skip DC bin (k=0); restrict to display range.
        for (var i = 1; i < spec.Frequencies.Length; i++)
        {
            var f = spec.Frequencies[i];
            if (f < minHz) continue;
            if (f > maxHz) break;

            var amp = spec.Amplitudes[i];
            var db = amp > 0
                ? 20.0 * Math.Log10(amp / ReferenceAmplitudeMm)
                : FloorDb;
            if (db < FloorDb) db = FloorDb;
            if (db > yMax) yMax = db;
            if (db < yMin) yMin = db;

            xs.Add(Math.Log10(f));
            ys.Add(db);
        }

        if (xs.Count == 0) return (FloorDb, FloorDb);

        var line = Plot.Add.Scatter(xs.ToArray(), ys.ToArray());
        line.Color = color;
        line.MarkerStyle.IsVisible = false;
        line.LineStyle.Width = 2;
        line.LineStyle.Pattern = LinePattern.Solid;

        if (!PeakMarkersEnabled) return (yMin, yMax);

        var (peakF, peakA) = TelemetryData.FindDominantPeak(spec, peakMinHz, peakMaxHz);
        if (!double.IsNaN(peakF) && peakA > 0)
        {
            var peakX = Math.Log10(peakF);
            var peakY = Math.Max(20.0 * Math.Log10(peakA / ReferenceAmplitudeMm), FloorDb);

            var marker = Plot.Add.Marker(peakX, peakY);
            marker.MarkerShape = MarkerShape.OpenCircle;
            marker.MarkerSize = 8;
            marker.MarkerLineWidth = 2;
            marker.Color = color;

            var label = Plot.Add.Text($"{peakF:0.00} Hz", peakX, peakY);
            label.LabelFontColor = color;
            label.LabelFontSize = 11;
            label.LabelAlignment = Alignment.LowerLeft;
            label.LabelOffsetX = 6;
            label.LabelOffsetY = -4;
        }

        return (yMin, yMax);
    }

    private void AddCornerLegend(double xRight, double yTop)
    {
        var span = yTop - Plot.Axes.GetLimits().Bottom;
        var y1 = yTop - span * 0.05;
        var y2 = yTop - span * 0.13;

        var frontLegend = Plot.Add.Text("Front", xRight, y1);
        frontLegend.LabelFontColor = FrontColor;
        frontLegend.LabelFontSize = 12;
        frontLegend.LabelAlignment = Alignment.UpperRight;
        frontLegend.LabelOffsetX = -6;

        var rearLegend = Plot.Add.Text("Rear", xRight, y2);
        rearLegend.LabelFontColor = RearColor;
        rearLegend.LabelFontSize = 12;
        rearLegend.LabelAlignment = Alignment.UpperRight;
        rearLegend.LabelOffsetX = -6;
    }

    private static NumericManual BuildLogTickGenerator(double minHz, double maxHz)
    {
        var gen = new NumericManual();
        var minDecade = (int)Math.Floor(Math.Log10(minHz));
        var maxDecade = (int)Math.Ceiling(Math.Log10(maxHz));

        // If the lower bound isn't a decade, add an extra major label there so
        // the start of the visible range is annotated (e.g. "3" for 3–100 Hz).
        var minIsDecade = Math.Abs(Math.Log10(minHz) - Math.Round(Math.Log10(minHz))) < 1e-9;
        if (!minIsDecade)
        {
            var label = minHz >= 1 ? $"{minHz:0.##}" : $"{minHz:0.#}";
            gen.AddMajor(Math.Log10(minHz), label);
        }

        for (var d = minDecade; d <= maxDecade; d++)
        {
            var f = Math.Pow(10, d);
            if (f >= minHz && f <= maxHz)
            {
                var label = f >= 1 ? $"{f:0}" : $"{f:0.#}";
                gen.AddMajor(Math.Log10(f), label);
            }
        }
        for (var d = minDecade; d <= maxDecade; d++)
        {
            var decade = Math.Pow(10, d);
            for (var m = 2; m <= 9; m++)
            {
                var f = m * decade;
                if (f >= minHz && f <= maxHz && Math.Abs(f - minHz) > 1e-9)
                    gen.AddMinor(Math.Log10(f));
            }
        }
        return gen;
    }
}
