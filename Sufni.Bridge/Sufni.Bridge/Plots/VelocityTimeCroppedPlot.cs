using System;
using ScottPlot;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

public class VelocityTimeCroppedPlot(Plot plot, SuspensionType type) : TelemetryPlot(plot)
{
    private static readonly Color StatColor = Color.FromHex("#FFD700");

    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);

        var side = type == SuspensionType.Front ? telemetryData.Front : telemetryData.Rear;
        SetTitle(type == SuspensionType.Front
            ? "Front velocity over time"
            : "Rear velocity over time");
        Plot.Layout.Fixed(new PixelPadding(55, 14, 50, 40));
        Plot.Axes.Bottom.Label.Text = "Time (s)";
        Plot.Axes.Left.Label.Text = "Velocity (m/s)";

        if (!side.Present || side.Velocity is not { Length: > 0 })
            return;

        var sampleRate = telemetryData.SampleRate;
        var period = 1.0 / sampleRate;
        var color = type == SuspensionType.Front ? FrontColor : RearColor;

        var v = new double[side.Velocity.Length];
        double vMax = double.NegativeInfinity;
        double vMin = double.PositiveInfinity;
        double sumSq = 0;
        for (int i = 0; i < v.Length; i++)
        {
            v[i] = side.Velocity[i] / 1000.0;
            if (v[i] > vMax) vMax = v[i];
            if (v[i] < vMin) vMin = v[i];
            sumSq += v[i] * v[i];
        }
        var rms = Math.Sqrt(sumSq / v.Length);

        var sig = Plot.Add.Signal(v, period);
        sig.Color = color;
        sig.LineWidth = 1;
        var maxDuration = v.Length * period;

        var span = Math.Max(vMax - vMin, 1e-9);
        var top    = vMax + span * 0.05;
        var bottom = vMin - span * 0.05;
        Plot.Axes.SetLimitsY(bottom: bottom, top: top);
        Plot.Axes.SetLimitsX(left: 0, right: maxDuration);

        Plot.Add.HorizontalLine(0, 1f, Color.FromHex("#dddddd"), LinePattern.Dotted);

        static string N(double val) =>
            val.ToString("F2").PadLeft(6).Replace(' ', ' ');

        var statsText =
            $"max: {N(vMax)}\n" +
            $"min: {N(vMin)}\n" +
            $"rms: {N(rms)}";

        var statsLabel = Plot.Add.Text(statsText, maxDuration, top);
        statsLabel.LabelFontColor = StatColor;
        statsLabel.LabelFontSize = 9;
        statsLabel.LabelFontName = "Menlo";
        statsLabel.LabelAlignment = Alignment.UpperRight;
        statsLabel.LabelOffsetX = -10;
        statsLabel.LabelOffsetY = 6;
        statsLabel.LabelBold = true;
        statsLabel.LabelBackgroundColor = Color.FromHex("#15191C").WithAlpha(220);
        statsLabel.LabelBorderColor = StatColor.WithAlpha(80);
        statsLabel.LabelBorderWidth = 1;
        statsLabel.LabelPadding = 5;
    }
}
