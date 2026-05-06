using System;
using System.Globalization;
using System.Linq;
using ScottPlot;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

/// <summary>
/// Force vs. velocity curve for one axle's damper. Shows every measured library curve thin
/// and faded in the background (so the user sees how their setup interpolates between
/// dyno measurements), plus the IDW-weighted curve effectively used by the force estimator
/// at the session's click configuration drawn bold on top.
///
/// X axis: signed velocity in mm/s (negative = rebound, positive = compression).
/// Y axis: signed force in N. A muted crosshair at the origin separates the four quadrants.
///
/// Data is supplied via <see cref="SetData"/>; the plot doesn't reach into DI itself.
/// </summary>
public class DamperCurvePlot(Plot plot, SuspensionType type) : TelemetryPlot(plot)
{
    private DamperLibrary? library;
    private DamperClicks clicks;

    private const int SamplePoints = 240;

    public void SetData(DamperLibrary library, DamperClicks clicks)
    {
        this.library = library;
        this.clicks = clicks;
    }

    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);
        var prefix = type == SuspensionType.Front ? "Front" : "Rear";
        SetTitle($"{prefix} damper curve");
        Plot.Layout.Fixed(new PixelPadding(60, 14, 50, 40));
        Plot.Axes.Bottom.Label.Text = "Velocity (mm/s)";
        Plot.Axes.Left.Label.Text = "Force (N)";

        if (library is null || library.Curves.Count == 0) return;

        // Velocity range = ±max(|v|) of all measured curves, with a small margin so
        // edge points aren't clipped against the axis.
        double vMax = 0;
        foreach (var c in library.Curves)
        {
            for (int i = 0; i < c.Velocity.Length; i++)
            {
                var av = Math.Abs(c.Velocity[i]);
                if (av > vMax) vMax = av;
            }
        }
        if (vMax <= 0) return;
        double vRange = vMax * 1.02;

        var color = type == SuspensionType.Front ? FrontColor : RearColor;
        var sampleVel = Enumerable.Range(0, SamplePoints)
            .Select(i => -vRange + 2 * vRange * i / (SamplePoints - 1.0))
            .ToArray();

        // Background: every measured curve, thinner and a bit faded so the effective
        // curve still stands out as the one rendered bold. Alpha 160 keeps the lines
        // legible on the dark plot background even when several curves overlap.
        double yMin = 0, yMax = 0;
        foreach (var c in library.Curves)
        {
            var ys = sampleVel.Select(v => c.InterpolateForce(v)).ToArray();
            var line = Plot.Add.Scatter(sampleVel, ys);
            line.Color = color.WithAlpha(160);
            line.LineWidth = 1;
            line.MarkerStyle.IsVisible = false;
            for (int i = 0; i < ys.Length; i++)
            {
                if (ys[i] < yMin) yMin = ys[i];
                if (ys[i] > yMax) yMax = ys[i];
            }
        }

        // Foreground: the effective IDW-interpolated curve at the session's clicks.
        var effForces = sampleVel.Select(v => library.EvaluateForce(v, clicks)).ToArray();
        var effLine = Plot.Add.Scatter(sampleVel, effForces);
        effLine.Color = color;
        effLine.LineWidth = 2.5f;
        effLine.MarkerStyle.IsVisible = false;

        // Origin crosshair — separates rebound (left/-y) from compression (right/+y).
        // Matches the y=0 dotted reference style used in the Damper-tab velocity plots
        // (VelocityTimeCroppedPlot) for visual consistency across the app.
        Plot.Add.HorizontalLine(0, 1f, Color.FromHex("#dddddd"), LinePattern.Dotted);
        Plot.Add.VerticalLine(0,   1f, Color.FromHex("#dddddd"), LinePattern.Dotted);

        var pad = (yMax - yMin) * 0.05;
        if (pad < 1) pad = 1;
        Plot.Axes.SetLimits(left: -vRange, right: vRange, bottom: yMin - pad, top: yMax + pad);

        AddClickLabel(vRange, yMin - pad, color);
    }

    private void AddClickLabel(double xRight, double yBottom, Color color)
    {
        // Click configuration the foreground curve was computed for, vertically stacked
        // top-to-bottom in HSC / LSC / LSR / HSR order — compression first, then rebound.
        // Values right-aligned in a fixed-width column for clean two-digit alignment.
        // Boxed background + border so the legend reads as a discrete element rather than
        // floating text overlapping the curves at the bottom of the plot.
        static string Fmt(string name, int? value) =>
            value.HasValue
                ? string.Format("{0}: {1,2}", name, value.Value)
                : string.Format("{0}:  —", name);
        var text = string.Join("\n",
            Fmt("HSC", clicks.Hsc),
            Fmt("LSC", clicks.Lsc),
            Fmt("LSR", clicks.Lsr),
            Fmt("HSR", clicks.Hsr));

        var label = Plot.Add.Text(text, xRight, yBottom);
        label.LabelFontColor = color;
        label.LabelFontSize = 10;
        label.LabelFontName = "Menlo";
        label.LabelBold = true;
        label.LabelAlignment = Alignment.LowerRight;
        label.LabelOffsetX = -10;
        label.LabelOffsetY = -6;
        label.LabelPadding = 5;
        label.LabelBackgroundColor = Color.FromHex("#15191C").WithAlpha(220);
        label.LabelBorderColor = color.WithAlpha(80);
        label.LabelBorderWidth = 1;
    }
}
