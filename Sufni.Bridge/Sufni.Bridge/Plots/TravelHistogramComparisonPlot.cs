using System;
using System.Linq;
using ScottPlot;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

public class TravelHistogramComparisonPlot(Plot plot) : TelemetryPlot(plot)
{
    private const double HistogramRangeMultiplier = 1.3;

    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);

        if (!telemetryData.Front.Present || !telemetryData.Rear.Present)
        {
            return;
        }

        Plot.Axes.Title.Label.Text = "Travel histogram comparison";
        Plot.Layout.Fixed(new PixelPadding(70, 10, 50, 40));

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

        var frontLegend = Plot.Add.Text("Front", 100, yRangeTop * 0.95);
        frontLegend.LabelFontColor = FrontColor;
        frontLegend.LabelFontSize = 12;
        frontLegend.LabelAlignment = Alignment.UpperRight;
        frontLegend.LabelOffsetX = -10; // 1em margin between label right edge and axis

        var rearLegend = Plot.Add.Text("Rear", 100, yRangeTop * 0.87);
        rearLegend.LabelFontColor = RearColor;
        rearLegend.LabelFontSize = 12;
        rearLegend.LabelAlignment = Alignment.UpperRight;
        rearLegend.LabelOffsetX = -10; // 1em margin between label right edge and axis
    }
}
