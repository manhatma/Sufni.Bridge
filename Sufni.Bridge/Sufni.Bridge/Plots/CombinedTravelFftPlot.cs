using System;
using System.Collections.Generic;
using System.Globalization;
using ScottPlot;
using ScottPlot.TickGenerators;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

public enum WheelSpectrumMode
{
    Travel,
    Velocity,
}

/// <summary>
/// Overlaid Welch-method spectrum of Front and Rear wheel motion.
/// Log-log axes: X = log10(frequency Hz), Y = amplitude in dB.
/// In Travel mode the Y axis is dB re 0.01 mm; in Velocity mode it is the
/// velocity amplitude (|v| = 2π·f·|x|) in dB re 1 mm/s. ScottPlot 5 has no
/// native log scale; we transform the data and use a NumericManual tick
/// generator to render decade labels back in Hz on the X axis.
///
/// Constructor parameters allow rendering different frequency windows from the
/// same underlying Welch spectrum (low/full range with body-peak markers, high
/// range without markers, etc.). Peak search is disabled when peakMaxHz <= peakMinHz.
/// </summary>
public class CombinedTravelFftPlot(
    Plot plot,
    double minHz = 0.1,
    double maxHz = 100.0,
    double peakMinHz = 0.5,
    double peakMaxHz = 5.0,
    int segmentLength = 8192,
    bool fitYAxisToData = false,
    double topHeadroomDb = 8.0,
    float lineWidth = 1.5f,
    WheelSpectrumMode mode = WheelSpectrumMode.Travel)
    : TelemetryPlot(plot)
{
    private const double TravelReferenceMm = 0.01;
    // STROKE_LENGTH_THRESHOLD from gosst/formats/psst/psst.go — the minimum
    // stroke length (mm) counted as a real compression/rebound by the import
    // pipeline. Used as the displacement anchor for the velocity reference.
    private const double StrokeLengthThresholdMm = 0.5;
    private const double FloorDb = -40.0;
    private const double AxisBottomDb = 0.0;
    private const double MinTopDb = 60.0;

    // Velocity reference: the velocity amplitude of a sinusoidal stroke at the
    // geometric center of the analysis band whose displacement amplitude equals
    // STROKE_LENGTH_THRESHOLD. So 0 dB corresponds to "spectral component
    // equivalent to a just-detectable stroke at the band's log-midpoint" —
    // band-aware and tied to the data-processing detection threshold.
    private double VelocityReferenceMmPerSec =>
        StrokeLengthThresholdMm * 2.0 * Math.PI * Math.Sqrt(minHz * maxHz);

    // Peak markers, vertical guidelines and rotated labels are part of the
    // velocity-spectrum interpretation (peak detection runs in the velocity
    // domain). The travel plot is purely a curve — no markers.
    private bool PeakMarkersEnabled =>
        peakMaxHz > peakMinHz && mode == WheelSpectrumMode.Velocity;

    // Peaks collected during AddSpectrum and labelled at the bottom of the plot
    // after axis limits are known.
    private readonly List<(double LogX, double F, Color Color)> _peaks = new();

    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);

        SetTitle(mode == WheelSpectrumMode.Velocity
            ? "Wheel velocity spectrum"
            : "Wheel travel spectrum");
        Plot.Layout.Fixed(new PixelPadding(55, 14, 50, 40));
        Plot.Axes.Bottom.Label.Text = "Frequency (Hz)";
        Plot.Axes.Left.Label.Text = mode == WheelSpectrumMode.Velocity
            ? string.Format(CultureInfo.InvariantCulture, "Velocity (dB re {0:0} mm/s)", VelocityReferenceMmPerSec)
            : "Amplitude (dB re 0.01 mm)";

        // SufniPlot hides minor ticks globally; for log X they're the visual cue
        // that the spacing between decades is logarithmic. Re-enable them on
        // the bottom axis only.
        Plot.Axes.Bottom.MinorTickStyle.Length = 4;
        Plot.Axes.Bottom.MinorTickStyle.Width = 1;
        Plot.Axes.Bottom.MinorTickStyle.Color = Color.FromHex("#505558");

        // Show minor grid lines at log-decade subdivisions (the X axis covers
        // a full decade, so the 2..9 minor ticks are useful visual references).
        // The Y-axis tick generator (NumericFixedInterval) emits only majors,
        // so this affects only the X axis in practice.
        Plot.Grid.MinorLineColor = Color.FromHex("#2E3438");
        Plot.Grid.MinorLineWidth = 1;

        var sampleRate = telemetryData.SampleRate;
        double yMaxDb = FloorDb, yMinDb = double.PositiveInfinity;
        var drewAny = false;

        if (telemetryData.Front.Present && telemetryData.Front.Travel is { Length: >= 64 })
        {
            var (lo, hi) = AddSpectrum(telemetryData.Front.Travel, sampleRate, FrontColor);
            yMaxDb = Math.Max(yMaxDb, hi); yMinDb = Math.Min(yMinDb, lo);
            drewAny = true;
        }
        if (telemetryData.Rear.Present && telemetryData.Rear.Travel is { Length: >= 64 })
        {
            var (lo, hi) = AddSpectrum(telemetryData.Rear.Travel, sampleRate, RearColor);
            yMaxDb = Math.Max(yMaxDb, hi); yMinDb = Math.Min(yMinDb, lo);
            drewAny = true;
        }

        if (!drewAny) return;

        var xLeft = Math.Log10(minHz);
        var xRight = Math.Log10(maxHz);

        double yBottom, yTop;
        // Velocity plots use 5-dB ticks (range is naturally compressed once the
        // reference is in mm/s rather than 0.1 mm/s); travel plots use 10-dB.
        double tickStep = mode == WheelSpectrumMode.Velocity ? 5.0 : 10.0;
        if (fitYAxisToData)
        {
            yBottom = Math.Floor(yMinDb / tickStep) * tickStep;
            yTop = yMaxDb + topHeadroomDb;
            if (yTop - yBottom < 2 * tickStep) yTop = yBottom + 2 * tickStep;
        }
        else
        {
            yBottom = AxisBottomDb;
            yTop = Math.Max(yMaxDb, MinTopDb) + topHeadroomDb;
        }
        Plot.Axes.SetLimits(left: xLeft, right: xRight, bottom: yBottom, top: yTop);
        // SetLimits doesn't always pin the Y range tightly when later plottables
        // (legend text, peak labels) are added — pin it explicitly.
        Plot.Axes.Left.Min = yBottom;
        Plot.Axes.Left.Max = yTop;

        Plot.Axes.Bottom.TickGenerator = BuildLogTickGenerator(minHz, maxHz);
        Plot.Axes.Left.TickGenerator = new NumericFixedInterval(tickStep);

        // Legend in the upper-right corner — same pattern as BalancePlot.
        AddCornerLegend(xRight, yTop);

        // Peak labels are rotated 90° (reading bottom-to-top, parallel to the
        // vertical guidelines) and anchored near the bottom edge of the plot.
        AddPeakLabels(yBottom, yTop);
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
            // If two peaks sit very close in X (within ~0.05 decades), the
            // rotated labels would visually overlap each other. In that case,
            // push the second one further up so they sit at different heights.
            bool overlaps = prevLogX.HasValue && Math.Abs(p.LogX - prevLogX.Value) < 0.05;
            bool stack = overlaps && !prevWasOffset;

            double y = yBottom + span * (stack ? 0.20 : 0.04);

            var label = Plot.Add.Text(string.Format(CultureInfo.InvariantCulture, "{0:0.00} Hz", p.F), p.LogX, y);
            label.LabelFontColor = p.Color;
            label.LabelFontSize = 11;
            label.LabelRotation = -90;
            // UpperCenter + rotation −90: anchor at the bottom of the rotated
            // label, text extends upward. Negative X offset shifts the label
            // body to the left of the vertical guideline. ~9 px = roughly half
            // the rotated text height + 2 px breathing room.
            label.LabelAlignment = Alignment.UpperCenter;
            label.LabelOffsetX = -9;
            label.LabelOffsetY = -16;
            prevLogX = p.LogX;
            prevWasOffset = stack;
        }
    }

    /// <returns>(yMinDb, yMaxDbForScaling) of the data drawn within the visible
    /// X range. yMin scans the full visible range; yMax scans only the peak
    /// search band [peakMinHz, peakMaxHz] when peak markers are enabled (i.e. the
    /// velocity plot) — high-frequency noise outside the body-resonance band
    /// otherwise drives the upper axis bound and wastes vertical real estate.
    /// When peak markers are disabled (travel plot), yMax scans the full range.</returns>
    private (double, double) AddSpectrum(double[] signal, int sampleRate, Color color)
    {
        var spec = TelemetryData.ComputeWelchSpectrum(signal, sampleRate, segmentLength);
        if (spec.Frequencies.Length == 0) return (FloorDb, FloorDb);

        var xs = new List<double>(spec.Frequencies.Length);
        var ys = new List<double>(spec.Frequencies.Length);
        double yMax = FloorDb, yMin = double.PositiveInfinity;
        bool scaleToPeakBand = PeakMarkersEnabled;

        // Skip DC bin (k=0); restrict to display range.
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
            // Limit yMax to the peak-search band so out-of-band noise spikes
            // don't inflate the axis; on the travel plot (no peak markers) the
            // full visible range is used.
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
            // Marker sits on the curve we drew (Travel or Velocity).
            double peakValue = mode == WheelSpectrumMode.Velocity
                ? peakA * 2.0 * Math.PI * peakF
                : peakA;
            double peakRef = mode == WheelSpectrumMode.Velocity
                ? VelocityReferenceMmPerSec
                : TravelReferenceMm;
            var peakY = Math.Max(20.0 * Math.Log10(peakValue / peakRef), FloorDb);

            var marker = Plot.Add.Marker(peakX, peakY);
            marker.MarkerShape = MarkerShape.OpenCircle;
            marker.MarkerSize = 8;
            marker.MarkerLineWidth = 1.5f;
            marker.Color = color;

            // Vertical guideline through the peak, full plot height, dashed and thin.
            var vline = Plot.Add.VerticalLine(peakX);
            vline.Color = color;
            vline.LineStyle.Width = 1;
            vline.LineStyle.Pattern = LinePattern.Dashed;

            _peaks.Add((peakX, peakF, color));
        }

        return (yMin, yMax);
    }

    private void AddCornerLegend(double xRight, double yTop)
    {
        // Pixel-based vertical offsets so legend position is independent of
        // the Y data range (otherwise tight ranges push legends onto curves).
        var frontLegend = Plot.Add.Text("Front", xRight, yTop);
        frontLegend.LabelFontColor = FrontColor;
        frontLegend.LabelFontSize = 12;
        frontLegend.LabelAlignment = Alignment.UpperRight;
        frontLegend.LabelOffsetX = -6;
        frontLegend.LabelOffsetY = 4;

        var rearLegend = Plot.Add.Text("Rear", xRight, yTop);
        rearLegend.LabelFontColor = RearColor;
        rearLegend.LabelFontSize = 12;
        rearLegend.LabelAlignment = Alignment.UpperRight;
        rearLegend.LabelOffsetX = -6;
        rearLegend.LabelOffsetY = 22;
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
