using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ScottPlot;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

/// <summary>
/// Force vs. shock-axis travel curve for one axle's spring. Mirrors <see cref="DamperCurvePlot"/>
/// in convention: every measured library curve thin and faded in the background, plus the
/// effective curve at the session's (pressure, volume) configuration drawn bold on top.
///
/// X axis: travel in mm (shock-axis, monotonically positive).
/// Y axis: force in N (always positive for an air spring).
///
/// Data is supplied via <see cref="SetData"/>; the plot doesn't reach into DI itself.
/// </summary>
public class SpringCurvePlot(Plot plot, SuspensionType type) : TelemetryPlot(plot)
{
    private SpringLibrary? library;
    private double pressurePsi;
    private double volumeCcm;

    private const int SamplePoints = 240;

    public void SetData(SpringLibrary library, double pressurePsi, double volumeCcm)
    {
        this.library = library;
        this.pressurePsi = pressurePsi;
        this.volumeCcm = volumeCcm;
    }

    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);
        var prefix = type == SuspensionType.Front ? "Front" : "Rear";
        SetTitle($"{prefix} spring curve");
        Plot.Layout.Fixed(new PixelPadding(60, 14, 50, 40));
        Plot.Axes.Bottom.Label.Text = "Travel (mm)";
        Plot.Axes.Left.Label.Text = "Force (N)";

        if (library is null || library.Curves.Count == 0) return;

        double tMax = 0;
        foreach (var c in library.Curves)
            for (int i = 0; i < c.Travel.Length; i++)
                if (c.Travel[i] > tMax) tMax = c.Travel[i];
        if (tMax <= 0) return;

        var color = type == SuspensionType.Front ? FrontColor : RearColor;
        var sampleT = Enumerable.Range(0, SamplePoints)
            .Select(i => tMax * i / (SamplePoints - 1.0))
            .ToArray();

        // Background: every measured curve thin + faded so the effective curve stands out.
        double yMin = 0, yMax = 0;
        foreach (var c in library.Curves)
        {
            var ys = sampleT.Select(t => c.InterpolateForce(t)).ToArray();
            var line = Plot.Add.Scatter(sampleT, ys);
            line.Color = color.WithAlpha(160);
            line.LineWidth = 1;
            line.MarkerStyle.IsVisible = false;
            for (int i = 0; i < ys.Length; i++)
            {
                if (ys[i] < yMin) yMin = ys[i];
                if (ys[i] > yMax) yMax = ys[i];
            }
        }

        // Foreground: the effective curve at the session's (pressure, volume).
        var effForces = sampleT.Select(t => library.EvaluateForce(t, pressurePsi, volumeCcm)).ToArray();
        var effLine = Plot.Add.Scatter(sampleT, effForces);
        effLine.Color = color;
        effLine.LineWidth = 2.5f;
        effLine.MarkerStyle.IsVisible = false;
        for (int i = 0; i < effForces.Length; i++)
        {
            if (effForces[i] < yMin) yMin = effForces[i];
            if (effForces[i] > yMax) yMax = effForces[i];
        }

        var pad = (yMax - yMin) * 0.05;
        if (pad < 1) pad = 1;
        Plot.Axes.SetLimits(left: 0, right: tMax, bottom: yMin - pad, top: yMax + pad);

        AddOpPointLabel(tMax, yMin - pad, color);
    }

    private void AddOpPointLabel(double xRight, double yBottom, Color color)
    {
        // Operating-point summary (pressure, volume spacers) the foreground curve was
        // computed for. Three columns: label (left-aligned), value (right-aligned), unit
        // (left-aligned). Menlo monospace + fixed-width fields keep the columns aligned
        // across rows. Same boxed visual style as the damper-curve clicks label.
        static string Row(string label, string value, string unit) =>
            string.Format(CultureInfo.InvariantCulture, "{0,-4} {1,5} {2}", label, value, unit);
        var text = string.Join("\n",
            Row("p:",   pressurePsi.ToString("0",   CultureInfo.InvariantCulture), "psi"),
            Row("vol:", volumeCcm  .ToString("0.#", CultureInfo.InvariantCulture), "cc"));

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
