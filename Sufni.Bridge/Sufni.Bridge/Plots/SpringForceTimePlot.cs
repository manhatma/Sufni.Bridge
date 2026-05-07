using System;
using System.Collections.Generic;
using ScottPlot;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

public class SpringForceTimePlot(Plot plot, SuspensionType type, bool showBottomAxis = true) : TelemetryPlot(plot)
{
    private double[]? springForce;
    private double? userYMin, userYMax;

    public void SetForceData(double[]? springForce)
    {
        this.springForce = springForce;
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
        var titleString = $"{prefix} spring force over time";
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

        if (springForce is null || springForce.Length == 0) return;
        var sampleRate = telemetryData.SampleRate;
        if (sampleRate <= 0) return;
        var period = 1.0 / sampleRate;
        var n = springForce.Length;

        double yMaxData = double.NegativeInfinity;
        double yMinData = double.PositiveInfinity;
        for (int i = 0; i < n; i++)
        {
            if (springForce[i] > yMaxData) yMaxData = springForce[i];
            if (springForce[i] < yMinData) yMinData = springForce[i];
        }
        if (double.IsInfinity(yMaxData)) { yMaxData = 1; yMinData = 0; }

        var sig = Plot.Add.Signal(springForce, period);
        sig.Color = type == SuspensionType.Front ? FrontColor : RearColor;
        sig.LineWidth = 1;

        var duration = n * period;
        var span = Math.Max(yMaxData - yMinData, 1.0);
        var yMin = userYMin ?? (yMinData - span * 0.05);
        var yMax = userYMax ?? (yMaxData + span * 0.05);
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
