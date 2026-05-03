using System;
using System.Collections.Generic;
using System.Globalization;
using ScottPlot;
using ScottPlot.TickGenerators;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

/// <summary>
/// Compare-mode counterpart to <see cref="CombinedTravelFftPlot"/>: overlays the
/// Welch spectrum of one suspension axis (Front OR Rear) across multiple
/// sessions on the same log-log axes. Each session uses its own colour.
/// In Velocity mode, body-resonance peak markers + frequency labels are drawn
/// per session so peak shifts between sessions are immediately visible.
/// </summary>
public class CompareSpectrumPlot(
    Plot plot,
    SuspensionType type,
    double minHz = 1.0,
    double maxHz = 10.0,
    double peakMinHz = 1.3,
    double peakMaxHz = 4.5,
    int segmentLength = 8192,
    double topHeadroomDb = 2.0,
    float lineWidth = 2f,
    WheelSpectrumMode mode = WheelSpectrumMode.Travel)
    : SufniPlot(plot)
{
    private const double TravelReferenceMm = 0.01;
    private const double StrokeLengthThresholdMm = 0.5;
    private const double FloorDb = -40.0;

    // See CombinedTravelFftPlot for the rationale: 0 dB corresponds to the
    // velocity amplitude of a sinusoidal stroke at the band's geometric centre
    // whose displacement equals STROKE_LENGTH_THRESHOLD.
    private double VelocityReferenceMmPerSec =>
        StrokeLengthThresholdMm * 2.0 * Math.PI * Math.Sqrt(minHz * maxHz);

    private bool PeakMarkersEnabled =>
        peakMaxHz > peakMinHz && mode == WheelSpectrumMode.Velocity;

    // Peaks collected during AddSpectrum and labelled at the bottom of the plot
    // after axis limits are known.
    private readonly List<(double LogX, double F, Color Color)> _peaks = new();

    public void LoadMultipleSessions(List<(TelemetryData data, Color color, LinePattern pattern, string name)> sessions)
    {
        var axisName = type == SuspensionType.Front ? "Front" : "Rear";
        SetTitle(mode == WheelSpectrumMode.Velocity
            ? $"{axisName} velocity spectrum"
            : $"{axisName} travel spectrum");
        Plot.Layout.Fixed(new PixelPadding(55, 14, 50, 40));
        Plot.Axes.Bottom.Label.Text = "Frequency (Hz)";
        Plot.Axes.Left.Label.Text = mode == WheelSpectrumMode.Velocity
            ? string.Format(CultureInfo.InvariantCulture, "Velocity (dB re {0:0} mm/s)", VelocityReferenceMmPerSec)
            : "Amplitude (dB re 0.01 mm)";

        // SufniPlot hides minor ticks globally; re-enable on the bottom axis
        // only — they're the visual cue for log decade subdivisions.
        Plot.Axes.Bottom.MinorTickStyle.Length = 4;
        Plot.Axes.Bottom.MinorTickStyle.Width = 1;
        Plot.Axes.Bottom.MinorTickStyle.Color = Color.FromHex("#505558");

        // Minor grid lines for the log-decade subdivisions (X axis only —
        // Y uses NumericFixedInterval which emits no minors).
        Plot.Grid.MinorLineColor = Color.FromHex("#2E3438");
        Plot.Grid.MinorLineWidth = 1;

        double yMaxDb = FloorDb, yMinDb = double.PositiveInfinity;
        var drewAny = false;

        foreach (var (data, color, _, _) in sessions)
        {
            var suspension = type == SuspensionType.Front ? data.Front : data.Rear;
            if (!suspension.Present || suspension.Travel is not { Length: >= 64 }) continue;
            var (lo, hi) = AddSpectrum(suspension.Travel, data.SampleRate, color);
            yMaxDb = Math.Max(yMaxDb, hi); yMinDb = Math.Min(yMinDb, lo);
            drewAny = true;
        }

        if (!drewAny) return;

        var xLeft = Math.Log10(minHz);
        var xRight = Math.Log10(maxHz);

        // Velocity plots use 5-dB ticks (range is naturally compressed once the
        // reference is in mm/s rather than 0.1 mm/s); travel plots use 10-dB.
        double tickStep = mode == WheelSpectrumMode.Velocity ? 5.0 : 10.0;
        double yBottom = Math.Floor(yMinDb / tickStep) * tickStep;
        double yTop = yMaxDb + topHeadroomDb;
        if (yTop - yBottom < 2 * tickStep) yTop = yBottom + 2 * tickStep;

        Plot.Axes.SetLimits(left: xLeft, right: xRight, bottom: yBottom, top: yTop);
        Plot.Axes.Left.Min = yBottom;
        Plot.Axes.Left.Max = yTop;

        Plot.Axes.Bottom.TickGenerator = BuildLogTickGenerator(minHz, maxHz);
        Plot.Axes.Left.TickGenerator = new NumericFixedInterval(tickStep);

        AddSessionLegend(xRight, yTop, sessions);
        AddPeakLabels(yBottom, yTop);
    }

    private (double, double) AddSpectrum(double[] signal, int sampleRate, Color color)
    {
        var spec = TelemetryData.ComputeWelchSpectrum(signal, sampleRate, segmentLength);
        if (spec.Frequencies.Length == 0) return (FloorDb, FloorDb);

        var xs = new List<double>(spec.Frequencies.Length);
        var ys = new List<double>(spec.Frequencies.Length);
        double yMax = FloorDb, yMin = double.PositiveInfinity;
        bool scaleToPeakBand = PeakMarkersEnabled;

        for (var i = 1; i < spec.Frequencies.Length; i++)
        {
            var f = spec.Frequencies[i];
            if (f < minHz) continue;
            if (f > maxHz) break;

            var amp = spec.Amplitudes[i];
            double value = mode == WheelSpectrumMode.Velocity
                ? amp * 2.0 * Math.PI * f
                : amp;
            double reference = mode == WheelSpectrumMode.Velocity
                ? VelocityReferenceMmPerSec
                : TravelReferenceMm;
            var db = value > 0 ? 20.0 * Math.Log10(value / reference) : FloorDb;
            if (db < FloorDb) db = FloorDb;
            if (db < yMin) yMin = db;
            // Restrict yMax to the peak-search band on velocity plots so that
            // out-of-band noise doesn't inflate the upper axis bound.
            if (!scaleToPeakBand || (f >= peakMinHz && f <= peakMaxHz))
            {
                if (db > yMax) yMax = db;
            }

            xs.Add(Math.Log10(f));
            ys.Add(db);
        }

        if (xs.Count == 0) return (FloorDb, FloorDb);

        var line = Plot.Add.Scatter(xs.ToArray(), ys.ToArray());
        line.Color = color;
        line.MarkerStyle.IsVisible = false;
        line.LineStyle.Width = lineWidth;
        line.LineStyle.Pattern = LinePattern.Solid;

        if (!PeakMarkersEnabled) return (yMin, yMax);

        var (peakF, peakA) = TelemetryData.FindDominantPeak(spec, peakMinHz, peakMaxHz);
        if (!double.IsNaN(peakF) && peakA > 0)
        {
            var peakX = Math.Log10(peakF);
            double peakValue = peakA * 2.0 * Math.PI * peakF;
            var peakY = Math.Max(20.0 * Math.Log10(peakValue / VelocityReferenceMmPerSec), FloorDb);

            var marker = Plot.Add.Marker(peakX, peakY);
            marker.MarkerShape = MarkerShape.OpenCircle;
            marker.MarkerSize = 8;
            marker.MarkerLineWidth = 1.5f;
            marker.Color = color;

            var vline = Plot.Add.VerticalLine(peakX);
            vline.Color = color;
            vline.LineStyle.Width = 1;
            vline.LineStyle.Pattern = LinePattern.Dashed;

            _peaks.Add((peakX, peakF, color));
        }

        return (yMin, yMax);
    }

    private void AddPeakLabels(double yBottom, double yTop)
    {
        if (_peaks.Count == 0) return;
        double span = yTop - yBottom;
        _peaks.Sort((a, b) => a.LogX.CompareTo(b.LogX));
        double? prevLogX = null;
        bool prevWasOffset = false;
        foreach (var p in _peaks)
        {
            // Stack overlapping labels so they sit at different heights.
            bool overlaps = prevLogX.HasValue && Math.Abs(p.LogX - prevLogX.Value) < 0.05;
            bool stack = overlaps && !prevWasOffset;

            double y = yBottom + span * (stack ? 0.20 : 0.04);

            var label = Plot.Add.Text(string.Format(CultureInfo.InvariantCulture, "{0:0.00} Hz", p.F), p.LogX, y);
            label.LabelFontColor = p.Color;
            label.LabelFontSize = 11;
            label.LabelRotation = -90;
            label.LabelAlignment = Alignment.UpperCenter;
            label.LabelOffsetX = -9;
            label.LabelOffsetY = -16;
            prevLogX = p.LogX;
            prevWasOffset = stack;
        }
    }

    private void AddSessionLegend(double xRight, double yTop,
        List<(TelemetryData data, Color color, LinePattern pattern, string name)> sessions)
    {
        // Pixel-anchored legend (independent of the data range) — one row per
        // session in its own colour. Same pattern as CombinedTravelFftPlot.
        const int firstOffset = 4;
        const int rowHeight = 18;
        for (var i = 0; i < sessions.Count; i++)
        {
            var (_, color, _, name) = sessions[i];
            var label = Plot.Add.Text(name, xRight, yTop);
            label.LabelFontColor = color;
            label.LabelFontSize = 12;
            label.LabelAlignment = Alignment.UpperRight;
            label.LabelOffsetX = -6;
            label.LabelOffsetY = firstOffset + i * rowHeight;
        }
    }

    private static NumericManual BuildLogTickGenerator(double minHz, double maxHz)
    {
        var gen = new NumericManual();
        var minDecade = (int)Math.Floor(Math.Log10(minHz));
        var maxDecade = (int)Math.Ceiling(Math.Log10(maxHz));

        var minIsDecade = Math.Abs(Math.Log10(minHz) - Math.Round(Math.Log10(minHz))) < 1e-9;
        if (!minIsDecade)
        {
            var fmt = minHz >= 1 ? "0.##" : "0.#";
            gen.AddMajor(Math.Log10(minHz), minHz.ToString(fmt, CultureInfo.InvariantCulture));
        }

        for (var d = minDecade; d <= maxDecade; d++)
        {
            var f = Math.Pow(10, d);
            if (f >= minHz && f <= maxHz)
            {
                var fmt = f >= 1 ? "0" : "0.#";
                gen.AddMajor(Math.Log10(f), f.ToString(fmt, CultureInfo.InvariantCulture));
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
