using System;
using System.Collections.Generic;
using ScottPlot;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

public class DamperForceTimePlot(Plot plot, SuspensionType type, bool showBottomAxis = true) : TelemetryPlot(plot)
{
    private double[]? damperForce;
    private double? userYMin, userYMax;

    public void SetForceData(double[]? damperForce)
    {
        this.damperForce = damperForce;
    }

    public void SetYRange(double yMin, double yMax)
    {
        userYMin = yMin;
        userYMax = yMax;
    }

    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);
        var prefix = type == SuspensionType.Front ? "Front" : "Rear";
        var titleString = $"{prefix} damper force over time";
        SetTitle("");
        Plot.Layout.Fixed(new PixelPadding(60, 14, showBottomAxis ? 50 : 7, 7));
        Plot.Axes.Left.Label.Text = "Force (N)";
        if (showBottomAxis)
        {
            Plot.Axes.Bottom.Label.Text = "Time (s)";
        }
        else
        {
            Plot.Axes.Bottom.Label.Text = "";
            Plot.Axes.Bottom.TickLabelStyle.ForeColor = Color.FromHex("#15191C");
        }

        if (damperForce is null || damperForce.Length == 0) return;
        var sampleRate = telemetryData.SampleRate;
        if (sampleRate <= 0) return;
        var period = 1.0 / sampleRate;
        var n = damperForce.Length;

        double yMaxData = double.NegativeInfinity;
        double yMinData = double.PositiveInfinity;
        for (int i = 0; i < n; i++)
        {
            if (damperForce[i] > yMaxData) yMaxData = damperForce[i];
            if (damperForce[i] < yMinData) yMinData = damperForce[i];
        }
        if (double.IsInfinity(yMaxData)) { yMaxData = 1; yMinData = 0; }

        var sig = Plot.Add.Signal(damperForce, period);
        sig.Color = type == SuspensionType.Front ? FrontColor : RearColor;
        sig.LineWidth = 1;

        var duration = n * period;
        var span = Math.Max(yMaxData - yMinData, 1.0);
        var yMin = userYMin ?? (yMinData - span * 0.05);
        // Larger top margin (or explicit value from caller) so the title overlay fits.
        var yMax = userYMax ?? (yMaxData + span * 0.20);
        Plot.Axes.SetLimits(left: 0, right: duration, bottom: yMin, top: yMax);

        var title = Plot.Add.Text(titleString, duration / 2, yMax);
        title.LabelFontColor = Color.FromHex("#D0D0D0");
        title.LabelFontSize = 13;
        title.LabelBold = true;
        title.LabelAlignment = Alignment.UpperCenter;
        title.LabelOffsetX = 0;
        title.LabelOffsetY = 6;
        title.LabelBackgroundColor = Color.FromHex("#15191C").WithAlpha(180);
    }
}
