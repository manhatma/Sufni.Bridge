using System;
using System.Collections.Generic;
using ScottPlot;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

/// <summary>
/// Wheel-load curve for one axle (Front or Rear) on a linear force axis. Linear so the
/// signed damper-force component (compression positive, rebound negative) is visible.
///
/// Reference lines:
///   - dashed yellow: trim wheel load — the operating-point / trail-median (not a true
///     static load since it's a median over dynamic data, but it's the reference value
///     the unloading metrics are normalised against).
///   - dotted red: 20 % unloading threshold (matches the time-domain metric).
///
/// Three signal curves are layered: damper (back), wheel-load sum (middle), spring
/// (front, drawn last so it's not occluded by the wheel-load curve which tracks it
/// closely under low velocity).
///
/// The damper signal is plotted with a +trim offset, so its zero aligns visually with
/// the trim reference line. Vertical distance from trim to the damper curve reads
/// directly as the instantaneous damper contribution to wheel load — matching the
/// physical decomposition F_wheel = F_spring + F_damper on the same numeric scale.
///
/// Force data is supplied externally (via <see cref="SetForceData"/>) — the plot doesn't
/// know how to invoke the force estimator and stays a pure renderer.
/// </summary>
public class WheelForceTimePlot(Plot plot, SuspensionType type) : TelemetryPlot(plot)
{
    private double[]? force;
    private double[]? springForce;
    private double[]? damperForce;
    private double staticForce;

    private static readonly Color UnloadingColor = Color.FromHex("#FF0000");
    // Same yellow as the statistic-marker lines in the travel histograms (#FFD700) so the
    // reference marker reads as a "reference" line consistent with the rest of the app.
    private static readonly Color TrimColor = Color.FromHex("#FFD700");
    private static readonly Color SpringColor = Color.FromHex("#FFA500"); // orange
    private static readonly Color DamperColor = Color.FromHex("#FF00FF"); // magenta

    public void SetForceData(double[]? force, double staticForce,
        double[]? springForce = null, double[]? damperForce = null)
    {
        this.force = force;
        this.springForce = springForce;
        this.damperForce = damperForce;
        this.staticForce = staticForce;
    }

    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);
        var prefix = type == SuspensionType.Front ? "Front" : "Rear";
        SetTitle($"{prefix} wheel load over time");
        Plot.Layout.Fixed(new PixelPadding(60, 14, 50, 40));
        Plot.Axes.Bottom.Label.Text = "Time (s)";
        Plot.Axes.Left.Label.Text = "Force (N)";

        if (force is null || force.Length == 0) return;
        var sampleRate = telemetryData.SampleRate;
        if (sampleRate <= 0) return;
        var period = 1.0 / sampleRate;
        var color = type == SuspensionType.Front ? FrontColor : RearColor;

        var n = force.Length;

        // The damper signal is plotted on the same Y axis as wheel-load and spring, but
        // offset by +trim so its zero line aligns visually with the trim reference. That
        // way the vertical distance between the damper curve and the trim line reads
        // directly as the damper's instantaneous contribution to the wheel load — the
        // approximation F_wheel − trim ≈ (F_spring − F_spring_at_trim) + F_damper, with
        // F_spring_at_trim ≈ trim near the static sag point.
        bool hasDamper = damperForce is not null && damperForce.Length == n;
        bool hasSpring = springForce is not null && springForce.Length == n;
        double[]? damperOffset = null;
        if (hasDamper)
        {
            damperOffset = new double[n];
            for (int i = 0; i < n; i++) damperOffset[i] = damperForce![i] + staticForce;
        }

        // Track Y range across all visible signals so the plot frame fits everything,
        // using the offset damper series rather than the raw signed values.
        double yMaxData = double.NegativeInfinity;
        double yMinData = double.PositiveInfinity;
        void Track(double[] arr)
        {
            for (int i = 0; i < arr.Length; i++)
            {
                if (arr[i] > yMaxData) yMaxData = arr[i];
                if (arr[i] < yMinData) yMinData = arr[i];
            }
        }
        Track(force);
        if (hasSpring) Track(springForce!);
        if (damperOffset is not null) Track(damperOffset);

        if (double.IsInfinity(yMaxData)) { yMaxData = 1; yMinData = 0; }

        // Draw order: damper (back) → wheel sum (middle) → spring (front). Spring on top
        // because it tracks the wheel-load curve closely; placing it last keeps it visible
        // wherever the two coincide.
        if (damperOffset is not null)
        {
            var sigDamper = Plot.Add.Signal(damperOffset, period);
            sigDamper.Color = DamperColor;
            sigDamper.LineWidth = 1;
        }
        var sig = Plot.Add.Signal(force, period);
        sig.Color = color;
        sig.LineWidth = 1;
        if (hasSpring)
        {
            var sigSpring = Plot.Add.Signal(springForce!, period);
            sigSpring.Color = SpringColor;
            sigSpring.LineWidth = 1;
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
            new() { LabelText = $"{prefix} load", LabelFontColor = color,       LabelBold = true },
        };
        if (hasSpring) items.Add(new() { LabelText = "Spring", LabelFontColor = SpringColor, LabelBold = true });
        if (hasDamper) items.Add(new() { LabelText = "Damper", LabelFontColor = DamperColor, LabelBold = true });

        Plot.Legend.IsVisible = true;
        Plot.Legend.Alignment = Alignment.UpperRight;
        Plot.Legend.BackgroundColor = Color.FromHex("#15191C").WithAlpha(220);
        Plot.Legend.OutlineColor = Color.FromHex("#505558");
        Plot.Legend.OutlineWidth = 1;
        Plot.Legend.FontSize = 10;
        Plot.Legend.Padding = new PixelPadding(6);
        Plot.Legend.SymbolWidth = 0;
        Plot.Legend.SymbolPadding = 0;
        Plot.Legend.DisplayPlottableLegendItems = false;
        Plot.Legend.ManualItems = items;
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
