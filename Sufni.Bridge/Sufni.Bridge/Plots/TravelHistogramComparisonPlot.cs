using System;
using System.Linq;
using ScottPlot;
using ScottPlot.TickGenerators;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

public class TravelHistogramComparisonPlot(Plot plot) : TelemetryPlot(plot)
{
    private const double HistogramRangeMultiplier = 1.3;

    private void AddStatLines(TelemetryData telemetryData, SuspensionType type, double yRangeTop)
    {
        var stats = telemetryData.CalculateDetailedTravelStatistics(type);
        var mx = type == SuspensionType.Front
            ? telemetryData.Linkage.MaxFrontTravel
            : telemetryData.Linkage.MaxRearTravel;

        if (mx <= 0) return;

        var avgPct = stats.Average / mx * 100.0;
        var p95Pct = stats.P95 / mx * 100.0;
        var maxPct = stats.Max / mx * 100.0;

        var color = type == SuspensionType.Front ? FrontColor : RearColor;

        Plot.Add.VerticalLine(avgPct, 2f, color, LinePattern.Dashed);
        Plot.Add.VerticalLine(p95Pct, 2f, color, LinePattern.Dashed);
        Plot.Add.VerticalLine(maxPct, 2f, color, LinePattern.Dashed);

        // Front labels: left of line (UpperRight alignment = text ends at the x position)
        // Rear labels:  right of line (UpperLeft alignment = text starts at the x position), lower y
        bool isFront = type == SuspensionType.Front;
        var labelY = isFront ? yRangeTop * 0.92 : yRangeTop * 0.84;
        var labelAlignment = isFront ? Alignment.UpperRight : Alignment.UpperLeft;
        var xOffset = isFront ? -4 : 4;
        var labelOffsetY = isFront ? 4 : 10;

        void AddLabel(string text, double x)
        {
            var t = Plot.Add.Text(text, x, labelY);
            t.LabelFontColor = color;
            t.LabelFontSize = 11;
            t.LabelAlignment = Alignment.LowerCenter;
            t.LabelRotation = -90;
            t.LabelOffsetX = 0;
            t.LabelOffsetY = labelOffsetY;
        }

        AddLabel("avg", avgPct);
        AddLabel("95th", p95Pct);
        AddLabel("max", maxPct);
    }

    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);

        if (!telemetryData.Front.Present || !telemetryData.Rear.Present)
        {
            return;
        }

        SetTitle("Travel histogram comparison");
        Plot.Layout.Fixed(new PixelPadding(50, 24, 50, 40));

        Plot.Axes.Bottom.Label.Text = "Travel (%)";
        Plot.Axes.Left.Label.Text = "Time (%)";

        var frontData = telemetryData.CalculateDetailedTravelHistogram(SuspensionType.Front);
        var rearData = telemetryData.CalculateDetailedTravelHistogram(SuspensionType.Rear);

        var maxFront = frontData.TimePercentage.Count > 0 ? frontData.TimePercentage.Max() : 1.0;
        var maxRear = rearData.TimePercentage.Count > 0 ? rearData.TimePercentage.Max() : 1.0;
        var yRangeTop = Math.Max(1.0, Math.Max(maxFront, maxRear)) * HistogramRangeMultiplier;

        var frontBars = frontData.TravelMidsPercentage.Zip(frontData.TimePercentage)
            .Select(tuple => new Bar
            {
                Position = tuple.First,
                Value = tuple.Second,
                FillColor = FrontColor.WithOpacity(),
                LineColor = FrontColor,
                LineWidth = 1f,
                Orientation = Orientation.Vertical,
                Size = frontData.BarWidthsPercentage.Count > 0 ? frontData.BarWidthsPercentage[0] : 0.0,
            })
            .ToList();

        for (var i = 0; i < frontBars.Count && i < frontData.BarWidthsPercentage.Count; i++)
        {
            frontBars[i].Size = frontData.BarWidthsPercentage[i];
        }

        var rearBars = rearData.TravelMidsPercentage.Zip(rearData.TimePercentage)
            .Select(tuple => new Bar
            {
                Position = tuple.First,
                Value = tuple.Second,
                FillColor = RearColor.WithOpacity(),
                LineColor = RearColor,
                LineWidth = 1f,
                Orientation = Orientation.Vertical,
                Size = rearData.BarWidthsPercentage.Count > 0 ? rearData.BarWidthsPercentage[0] : 0.0,
            })
            .ToList();

        for (var i = 0; i < rearBars.Count && i < rearData.BarWidthsPercentage.Count; i++)
        {
            rearBars[i].Size = rearData.BarWidthsPercentage[i];
        }

        if (frontBars.Count > 0)
        {
            Plot.Add.Bars(frontBars);
        }

        if (rearBars.Count > 0)
        {
            Plot.Add.Bars(rearBars);
        }

        Plot.Axes.SetLimits(left: 0, right: 100, bottom: 0, top: yRangeTop);
        Plot.Axes.Bottom.TickGenerator = new NumericManual(
            [0.0, 10.0, 20.0, 30.0, 40.0, 50.0, 60.0, 70.0, 80.0, 90.0, 100.0],
            ["0", "10", "20", "30", "40", "50", "60", "70", "80", "90", "100"]);

        // Legend — top-left
        var frontLegend = Plot.Add.Text("Front", 2, yRangeTop * 0.97);
        frontLegend.LabelFontColor = FrontColor;
        frontLegend.LabelFontSize = 12;
        frontLegend.LabelAlignment = Alignment.UpperLeft;

        var rearLegend = Plot.Add.Text("Rear", 2, yRangeTop * 0.87);
        rearLegend.LabelFontColor = RearColor;
        rearLegend.LabelFontSize = 12;
        rearLegend.LabelAlignment = Alignment.UpperLeft;

        // Stat lines (front above, rear below so labels don't overlap)
        AddStatLines(telemetryData, SuspensionType.Front, yRangeTop);
        AddStatLines(telemetryData, SuspensionType.Rear, yRangeTop);
    }
}
