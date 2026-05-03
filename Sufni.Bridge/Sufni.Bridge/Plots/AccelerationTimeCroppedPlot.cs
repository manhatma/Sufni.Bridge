using System;
using ScottPlot;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

public class AccelerationTimeCroppedPlot(Plot plot) : TelemetryPlot(plot)
{
    private const double GravityMmPerS2 = 9806.65;

    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);

        SetTitle("Acceleration over time");
        Plot.Layout.Fixed(new PixelPadding(55, 14, 50, 40));
        Plot.Axes.Bottom.Label.Text = "Time (s)";
        Plot.Axes.Left.Label.Text = "Acceleration (g)";

        var sampleRate = telemetryData.SampleRate;
        var period = 1.0 / sampleRate;

        double maxDuration = 0;
        double aMax = double.NegativeInfinity;
        double aMin = double.PositiveInfinity;

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

        void Track(double[] a)
        {
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] > aMax) aMax = a[i];
                if (a[i] < aMin) aMin = a[i];
            }
        }

        (double Max, double Min, double Rms)? frontStats = null;
        (double Max, double Min, double Rms)? rearStats = null;

        // Rear first, so Front is drawn on top.
        if (telemetryData.Rear.Present && telemetryData.Rear.Velocity is { Length: > 0 })
        {
            var accRear = ToAcceleration(telemetryData.Rear.Velocity);
            var sig = Plot.Add.Signal(accRear, period);
            sig.Color = RearColor;
            sig.LineWidth = 1;
            maxDuration = Math.Max(maxDuration, accRear.Length * period);
            Track(accRear);
            rearStats = Stats(accRear);
        }

        if (telemetryData.Front.Present && telemetryData.Front.Velocity is { Length: > 0 })
        {
            var accFront = ToAcceleration(telemetryData.Front.Velocity);
            var sig = Plot.Add.Signal(accFront, period);
            sig.Color = FrontColor;
            sig.LineWidth = 1;
            maxDuration = Math.Max(maxDuration, accFront.Length * period);
            Track(accFront);
            frontStats = Stats(accFront);
        }

        if (double.IsInfinity(aMax)) { aMax = 1; aMin = -1; }
        var span = Math.Max(aMax - aMin, 1e-9);
        var top    = aMax + span * 0.05;
        var bottom = aMin - span * 0.05;
        Plot.Axes.SetLimitsY(bottom: bottom, top: top);

        if (maxDuration > 0)
            Plot.Axes.SetLimitsX(left: 0, right: maxDuration);

        Plot.Add.HorizontalLine(0, 1f, Color.FromHex("#dddddd"), LinePattern.Dotted);

        // Legend — upper-left corner
        var range = top - bottom;
        var frontLegend = Plot.Add.Text("Front", 0, top);
        frontLegend.LabelFontColor = FrontColor;
        frontLegend.LabelFontSize = 12;
        frontLegend.LabelAlignment = Alignment.UpperLeft;
        frontLegend.LabelOffsetX = 6;
        frontLegend.LabelOffsetY = 6;

        var rearLegend = Plot.Add.Text("Rear", 0, top - range * 0.08);
        rearLegend.LabelFontColor = RearColor;
        rearLegend.LabelFontSize = 12;
        rearLegend.LabelAlignment = Alignment.UpperLeft;
        rearLegend.LabelOffsetX = 6;
        rearLegend.LabelOffsetY = 6;

        // Stats readouts (max/min/rms) — upper-right per side, stacked
        var rightX = maxDuration > 0 ? maxDuration : 1.0;

        static string N(double val) => val.ToString("F1").PadLeft(5).Replace(' ', ' ');

        void AddStats((double Max, double Min, double Rms) s, Color color, double anchorY, Alignment alignment, int offsetY)
        {
            var text =
                $"max: {N(s.Max)}\n" +
                $"min: {N(s.Min)}\n" +
                $"rms: {N(s.Rms)}";
            var label = Plot.Add.Text(text, rightX, anchorY);
            label.LabelFontColor = color;
            label.LabelFontSize = 9;
            label.LabelFontName = "Menlo";
            label.LabelAlignment = alignment;
            label.LabelOffsetX = -10;
            label.LabelOffsetY = offsetY;
            label.LabelBold = true;
            label.LabelBackgroundColor = Color.FromHex("#15191C").WithAlpha(220);
            label.LabelBorderColor = color.WithAlpha(80);
            label.LabelBorderWidth = 1;
            label.LabelPadding = 5;
        }

        if (frontStats is not null)
            AddStats(frontStats.Value, FrontColor, top, Alignment.UpperRight, 6);
        if (rearStats is not null)
            AddStats(rearStats.Value, RearColor, bottom, Alignment.LowerRight, -6);
    }
}
