using System;
using ScottPlot;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

public class AccelerationTimeCroppedPlot(Plot plot, SuspensionType type) : TelemetryPlot(plot)
{
    private const double GravityMmPerS2 = 9806.65;

    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);

        var prefix = type == SuspensionType.Front ? "Front" : "Rear";
        SetTitle($"{prefix} acceleration over time");
        Plot.Layout.Fixed(new PixelPadding(55, 14, 50, 40));
        Plot.Axes.Bottom.Label.Text = "Time (s)";
        Plot.Axes.Left.Label.Text = "Acceleration (g)";

        var sampleRate = telemetryData.SampleRate;
        var period = 1.0 / sampleRate;

        double[] ToAcceleration(double[] v)
        {
            var n = v.Length;
            var a = new double[n];
            if (n < 2) return a;
            a[0] = (v[1] - v[0]) * sampleRate / GravityMmPerS2;
            for (int i = 1; i < n - 1; i++)
                a[i] = (v[i + 1] - v[i - 1]) * sampleRate / 2.0 / GravityMmPerS2;
            a[n - 1] = (v[n - 1] - v[n - 2]) * sampleRate / GravityMmPerS2;
            return a;
        }

        static (double Max, double Min, double Rms) Stats(double[] a)
        {
            if (a.Length == 0) return (0, 0, 0);
            double mx = double.NegativeInfinity, mn = double.PositiveInfinity, sumSq = 0;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] > mx) mx = a[i];
                if (a[i] < mn) mn = a[i];
                sumSq += a[i] * a[i];
            }
            return (mx, mn, Math.Sqrt(sumSq / a.Length));
        }

        var sus = type == SuspensionType.Front ? telemetryData.Front : telemetryData.Rear;
        if (!sus.Present || sus.Velocity is not { Length: > 0 }) return;

        var color = type == SuspensionType.Front ? FrontColor : RearColor;
        var label = type == SuspensionType.Front ? "Front" : "Rear";

        var acc = ToAcceleration(sus.Velocity);
        var sig = Plot.Add.Signal(acc, period);
        sig.Color = color;
        sig.LineWidth = 1;
        var duration = acc.Length * period;
        var stats = Stats(acc);

        double aMax = double.NegativeInfinity, aMin = double.PositiveInfinity;
        for (int i = 0; i < acc.Length; i++)
        {
            if (acc[i] > aMax) aMax = acc[i];
            if (acc[i] < aMin) aMin = acc[i];
        }
        if (double.IsInfinity(aMax)) { aMax = 1; aMin = -1; }
        var span = Math.Max(aMax - aMin, 1e-9);
        var top    = aMax + span * 0.05;
        var bottom = aMin - span * 0.05;
        Plot.Axes.SetLimitsY(bottom: bottom, top: top);

        if (duration > 0)
            Plot.Axes.SetLimitsX(left: 0, right: duration);

        Plot.Add.HorizontalLine(0, 1f, Color.FromHex("#dddddd"), LinePattern.Dotted);

        // Legend — upper-left corner
        var legend = Plot.Add.Text(label, 0, top);
        legend.LabelFontColor = color;
        legend.LabelFontSize = 12;
        legend.LabelAlignment = Alignment.UpperLeft;
        legend.LabelOffsetX = 6;
        legend.LabelOffsetY = 6;

        // Stats readout (max/min/rms) — upper-right. Values formatted to a fixed-width
        // 7-char field so digits stay right-aligned across rows in the monospace font.
        var rightX = duration > 0 ? duration : 1.0;
        static string N(double val) => string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0,7:F1}", val);
        var text =
            $"max: {N(stats.Max)}\n" +
            $"min: {N(stats.Min)}\n" +
            $"rms: {N(stats.Rms)}";
        var statsLabel = Plot.Add.Text(text, rightX, top);
        statsLabel.LabelFontColor = color;
        statsLabel.LabelFontSize = 9;
        statsLabel.LabelFontName = "Menlo";
        statsLabel.LabelAlignment = Alignment.UpperRight;
        statsLabel.LabelOffsetX = -10;
        statsLabel.LabelOffsetY = 6;
        statsLabel.LabelBold = true;
        statsLabel.LabelBackgroundColor = Color.FromHex("#15191C").WithAlpha(220);
        statsLabel.LabelBorderColor = color.WithAlpha(80);
        statsLabel.LabelBorderWidth = 1;
        statsLabel.LabelPadding = 5;
    }
}
