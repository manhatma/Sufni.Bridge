using System;
using System.Collections.Generic;
using ScottPlot;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

/// <summary>
/// Wheel-load curve for one axle (Front or Rear) on a linear force axis.
///
/// Reference lines:
///   - dashed yellow: trim wheel load — the operating-point / trail-median (not a true
///     static load since it's a median over dynamic data, but it's the reference value
///     the unloading metrics are normalised against).
///   - dotted red: 20 % unloading threshold (matches the time-domain metric).
///
/// Force data is supplied externally (via <see cref="SetForceData"/>) — the plot doesn't
/// know how to invoke the force estimator and stays a pure renderer.
/// </summary>
public class WheelForceTimePlot(Plot plot, SuspensionType type, bool envelope = false) : TelemetryPlot(plot)
{
    private double[]? force;
    private double staticForce;

    // Window length for the sliding max / min in envelope mode. ~0.2 s smooths sample-
    // level jitter and the unsprung-mass band while still tracking the body-resonance
    // amplitude (1.5–3 Hz) and all terrain-scale excursions.
    private const double EnvelopeWindowSeconds = 0.2;

    private static readonly Color UnloadingColor = Color.FromHex("#FF0000");
    // Same yellow as the statistic-marker lines in the travel histograms (#FFD700) so the
    // reference marker reads as a "reference" line consistent with the rest of the app.
    private static readonly Color TrimColor = Color.FromHex("#FFD700");
    public void SetForceData(double[]? force, double staticForce)
    {
        this.force = force;
        this.staticForce = staticForce;
    }

    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);
        var prefix = type == SuspensionType.Front ? "Front" : "Rear";
        var titleSuffix = envelope ? " envelope" : "";
        SetTitle($"{prefix} wheel load{titleSuffix} over time");
        Plot.Layout.Fixed(new PixelPadding(60, 14, 50, 40));
        Plot.Axes.Bottom.Label.Text = "Time (s)";
        Plot.Axes.Left.Label.Text = "Force (N)";

        if (force is null || force.Length == 0) return;
        var sampleRate = telemetryData.SampleRate;
        if (sampleRate <= 0) return;
        var period = 1.0 / sampleRate;
        var color = type == SuspensionType.Front ? FrontColor : RearColor;

        var n = force.Length;

        // In envelope mode replace the wheel-load with its upper/lower envelope pair.
        int windowSamples = envelope
            ? Math.Max(3, (int)Math.Round(EnvelopeWindowSeconds * sampleRate))
            : 0;

        var seriesToDraw = new List<(double[] data, Color col)>();
        if (envelope)
        {
            {
                var (lo, hi) = SlidingEnvelope(force, windowSamples);
                seriesToDraw.Add((hi, color));
                seriesToDraw.Add((lo, color));
            }
        }
        else
        {
            seriesToDraw.Add((force, color));
        }

        // Track Y range across every series that will be drawn, so the plot frame fits
        // the actual content (envelope or raw).
        double yMaxData = double.NegativeInfinity;
        double yMinData = double.PositiveInfinity;
        foreach (var (arr, _) in seriesToDraw)
        {
            for (int i = 0; i < arr.Length; i++)
            {
                if (arr[i] > yMaxData) yMaxData = arr[i];
                if (arr[i] < yMinData) yMinData = arr[i];
            }
        }
        if (double.IsInfinity(yMaxData)) { yMaxData = 1; yMinData = 0; }

        // Draw in the order populated above. Each series uses 1 px so envelopes and raw share line weight.
        foreach (var (arr, col) in seriesToDraw)
        {
            var sig = Plot.Add.Signal(arr, period);
            sig.Color = col;
            sig.LineWidth = 1;
        }

        var duration = n * period;

        if (staticForce > 0)
        {
            var trimLine = Plot.Add.HorizontalLine(staticForce);
            trimLine.Color = TrimColor;
            trimLine.LineStyle.Width = 2;
            trimLine.LineStyle.Pattern = LinePattern.Dashed;

            var threshLine = Plot.Add.HorizontalLine(0.20 * staticForce);
            threshLine.Color = UnloadingColor;
            threshLine.LineStyle.Width = 2;
            threshLine.LineStyle.Pattern = LinePattern.Dotted;

            AddRefLineLabel("trim", staticForce, TrimColor);
            AddRefLineLabel("20%", 0.20 * staticForce, UnloadingColor);

            if (staticForce > yMaxData) yMaxData = staticForce;
            // Keep the 20 % unloading threshold visible at the bottom of the plot frame.
            if (0.20 * staticForce < yMinData) yMinData = 0.20 * staticForce;
        }

        // Linear Y range with 5 % padding on both ends.
        var span = Math.Max(yMaxData - yMinData, 1.0);
        var yMin = yMinData - span * 0.05;
        var yMax = yMaxData + span * 0.05;
        Plot.Axes.SetLimits(left: 0, right: duration, bottom: yMin, top: yMax);

        // Manual legend items so each line can be coloured to match its curve. Auto-built
        // items off (DisplayPlottableLegendItems = false) and SymbolWidth = 0 collapse the
        // colour-swatch column so the legend reads as a stack of pure colored text.
        var items = new List<LegendItem>
        {
            new() { LabelText = $"{prefix} load", LabelFontColor = color, LabelBold = true },
        };

        Plot.Legend.IsVisible = true;
        Plot.Legend.Alignment = Alignment.UpperRight;
        Plot.Legend.BackgroundColor = Color.FromHex("#15191C").WithAlpha(182);
        Plot.Legend.OutlineColor = Color.FromHex("#505558");
        Plot.Legend.OutlineWidth = 1;
        Plot.Legend.FontSize = 10;
        // +3 px top so the single legend line sits vertically centred in the box.
        Plot.Legend.Padding = new PixelPadding(6, 6, 6, 9);
        Plot.Legend.SymbolWidth = 0;
        Plot.Legend.SymbolPadding = 0;
        Plot.Legend.DisplayPlottableLegendItems = false;
        Plot.Legend.ManualItems = items;
    }

    /// <summary>
    /// Centred sliding max / min envelope over <paramref name="window"/> samples. Uses
    /// monotonic-deque tracking so the whole pass is O(n) regardless of window size.
    /// At the boundaries the window simply shrinks (no padding / extrapolation) — first
    /// and last samples reflect a half-window's worth of data.
    /// </summary>
    private static (double[] Lower, double[] Upper) SlidingEnvelope(double[] x, int window)
    {
        int n = x.Length;
        var lower = new double[n];
        var upper = new double[n];
        if (n == 0 || window <= 1)
        {
            for (int i = 0; i < n; i++) { lower[i] = x[i]; upper[i] = x[i]; }
            return (lower, upper);
        }
        int half = window / 2;
        var maxDq = new LinkedList<int>();
        var minDq = new LinkedList<int>();
        int rightEnd = -1;
        for (int i = 0; i < n; i++)
        {
            int wantRight = Math.Min(n - 1, i + half);
            while (rightEnd < wantRight)
            {
                rightEnd++;
                while (maxDq.Count > 0 && x[maxDq.Last!.Value] <= x[rightEnd]) maxDq.RemoveLast();
                maxDq.AddLast(rightEnd);
                while (minDq.Count > 0 && x[minDq.Last!.Value] >= x[rightEnd]) minDq.RemoveLast();
                minDq.AddLast(rightEnd);
            }
            int wantLeft = Math.Max(0, i - half);
            while (maxDq.Count > 0 && maxDq.First!.Value < wantLeft) maxDq.RemoveFirst();
            while (minDq.Count > 0 && minDq.First!.Value < wantLeft) minDq.RemoveFirst();
            upper[i] = x[maxDq.First!.Value];
            lower[i] = x[minDq.First!.Value];
        }
        return (lower, upper);
    }

    private void AddRefLineLabel(string content, double y, Color color)
    {
        var text = Plot.Add.Text(content, 0, y);
        text.LabelFontColor = color;
        text.LabelFontSize = 11;
        text.LabelBold = true;
        text.LabelAlignment = Alignment.LowerLeft;
        text.LabelOffsetX = 6;
        text.LabelOffsetY = -1;
    }

}
