using System;
using ScottPlot;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

public class CumulativeTravelPlot(Plot plot) : TelemetryPlot(plot)
{
    private const double MinTotalForShareMm = 500.0;
    private static readonly Color ShareColor = Color.FromHex("#D0D0D0");

    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);

        SetTitle("Cumulative travel");
        Plot.Layout.Fixed(new PixelPadding(55, 62, 50, 40));
        Plot.Axes.Bottom.Label.Text = "Time (s)";
        Plot.Axes.Left.Label.Text = "Cumulative travel (m)";

        var cumFrontMm = telemetryData.CalculateCumulativeTravel(SuspensionType.Front);
        var cumRearMm = telemetryData.CalculateCumulativeTravel(SuspensionType.Rear);
        var n = Math.Min(cumFrontMm.Length, cumRearMm.Length);
        if (n == 0)
        {
            AddLabel("Cumulative travel needs front and rear data", 0.5, 0.5, 0, 0, Alignment.MiddleCenter, "#aaaaaa");
            return;
        }

        var period = 1.0 / telemetryData.SampleRate;
        var frontM = new double[n];
        var rearM = new double[n];
        var share = new double[n];
        for (var i = 0; i < n; i++)
        {
            var f = cumFrontMm[i];
            var r = cumRearMm[i];
            frontM[i] = f / 1000.0;
            rearM[i] = r / 1000.0;
            var total = f + r;
            share[i] = total < MinTotalForShareMm ? double.NaN : f / total * 100.0;
        }

        var frontSig = Plot.Add.Signal(frontM, period);
        frontSig.Color = FrontColor;
        frontSig.LineWidth = 1;

        var rearSig = Plot.Add.Signal(rearM, period);
        rearSig.Color = RearColor;
        rearSig.LineWidth = 1;

        var rightAxis = Plot.Axes.Right;
        rightAxis.Label.Text = "Front share (%)";
        rightAxis.Label.Rotation = -90f;
        rightAxis.Label.ForeColor = Color.FromHex("#D0D0D0");
        rightAxis.Label.FontSize = 14;
        rightAxis.Label.Bold = false;
        rightAxis.Label.OffsetX = -10;
        rightAxis.TickLabelStyle.ForeColor = Color.FromHex("#D0D0D0");
        rightAxis.TickLabelStyle.FontSize = 12;

        var shareSig = Plot.Add.Signal(share, period);
        shareSig.Axes.YAxis = rightAxis;
        shareSig.Color = ShareColor;
        shareSig.LineWidth = 1;
        shareSig.LinePattern = LinePattern.Dashed;

        var refLine = Plot.Add.HorizontalLine(50, 1f, Color.FromHex("#444444"), LinePattern.Dotted);
        refLine.Axes.YAxis = rightAxis;
        rightAxis.Min = 0;
        rightAxis.Max = 100;

        var maxDuration = n * period;
        var maxY = Math.Max(frontM[^1], rearM[^1]);
        if (maxY <= 0) maxY = 1;
        Plot.Axes.SetLimitsX(left: 0, right: maxDuration);
        Plot.Axes.SetLimitsY(bottom: 0, top: maxY * 1.05);

        // Legend (upper-left — the cumulative curves start near 0 there and only approach
        // their maximum towards the right edge, so the corner stays clear of data).
        var legendX = maxDuration * 0.02;
        var legendYTop = maxY * 1.05 * 0.95;
        var legendStep = maxY * 1.05 * 0.08;

        var frontLegend = Plot.Add.Text("Front", legendX, legendYTop);
        frontLegend.LabelFontColor = FrontColor;
        frontLegend.LabelFontSize = 12;
        frontLegend.LabelAlignment = Alignment.UpperLeft;
        frontLegend.LabelOffsetX = 4;

        var rearLegend = Plot.Add.Text("Rear", legendX, legendYTop - legendStep);
        rearLegend.LabelFontColor = RearColor;
        rearLegend.LabelFontSize = 12;
        rearLegend.LabelAlignment = Alignment.UpperLeft;
        rearLegend.LabelOffsetX = 4;
        rearLegend.LabelOffsetY = 8;

        var shareLegend = Plot.Add.Text("Front share", legendX, legendYTop - legendStep * 2);
        shareLegend.LabelFontColor = ShareColor;
        shareLegend.LabelFontSize = 12;
        shareLegend.LabelAlignment = Alignment.UpperLeft;
        shareLegend.LabelOffsetX = 4;
        shareLegend.LabelOffsetY = 16;
    }
}
