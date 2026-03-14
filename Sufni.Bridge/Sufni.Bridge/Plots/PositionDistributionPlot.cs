using System;
using System.Linq;
using ScottPlot;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

public class PositionDistributionPlot(Plot plot, SuspensionType type) : TelemetryPlot(plot)
{
    private const double HistogramRangeMultiplier = 1.3;

    private void AddStatistics(TelemetryData telemetryData, double yRangeTop)
    {
        var statistics = telemetryData.CalculateDetailedTravelStatistics(type);

        var mx = type == SuspensionType.Front
            ? telemetryData.Linkage.MaxFrontTravel
            : telemetryData.Linkage.MaxRearTravel;
        var avgPercentage = mx > 0 ? statistics.Average / mx * 100.0 : 0.0;
        var maxPercentage = mx > 0 ? statistics.Max / mx * 100.0 : 0.0;
        var p95Percentage = mx > 0 ? statistics.P95 / mx * 100.0 : 0.0;

        Plot.Add.VerticalLine(statistics.Average, 2f, Color.FromHex("#FFD700"), LinePattern.Dashed);
        Plot.Add.VerticalLine(statistics.Max, 2f, Color.FromHex("#FFD700"), LinePattern.Dashed);
        Plot.Add.VerticalLine(statistics.P95, 2f, Color.FromHex("#FFD700"), LinePattern.Dashed);

        AddLabel("avg", statistics.Average, yRangeTop * 0.95, -8, 0, Alignment.MiddleRight);
        AddLabel("max", statistics.Max, yRangeTop * 0.95, -8, 0, Alignment.MiddleRight);
        AddLabel("95th", statistics.P95, yRangeTop * 0.95, -8, 0, Alignment.MiddleRight);

        var avgString = $"Avg:  {statistics.Average:F1} mm ({avgPercentage:F1}%)";
        var p95String = $"95th: {statistics.P95:F1} mm ({p95Percentage:F1}%)";
        var maxString = $"Max:  {statistics.Max:F1} mm ({maxPercentage:F1}%)";
        var boString = $"#BO:  {statistics.Bottomouts}";

        var statsLabel = Plot.Add.Text(
            $"{avgString}\n\n{p95String}\n\n{maxString}\n\n{boString}",
            mx,
            yRangeTop);
        statsLabel.LabelFontColor = Color.FromHex("#FFD700");
        statsLabel.LabelFontSize = 14;
        statsLabel.LabelAlignment = Alignment.UpperRight;
        statsLabel.LabelBold = true;
        statsLabel.LabelBackgroundColor = Color.FromHex("#15191C").WithAlpha(220);
        statsLabel.LabelBorderColor = Color.FromHex("#FFD700").WithAlpha(80);
        statsLabel.LabelBorderWidth = 1;
        statsLabel.LabelPadding = 12;
    }

    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);

        Plot.Axes.Title.Label.Text = type == SuspensionType.Front
            ? "Front position distribution"
            : "Rear position distribution";
        Plot.Layout.Fixed(new PixelPadding(70, 10, 50, 40));

        Plot.Axes.Bottom.Label.Text = "Travel (mm)";
        Plot.Axes.Left.Label.Text = "Time (%)";

        var data = telemetryData.CalculateDetailedTravelHistogram(type);
        var maxTime = data.TimePercentage.Count > 0 ? data.TimePercentage.Max() : 1.0;
        var yRangeTop = Math.Max(1.0, maxTime) * HistogramRangeMultiplier;
        var color = type == SuspensionType.Front ? FrontColor : RearColor;

        var bars = data.TravelMidsMm.Zip(data.TimePercentage)
            .Select(tuple => new Bar
            {
                Position = tuple.First,
                Value = tuple.Second,
                FillColor = color.WithOpacity(),
                LineColor = color,
                LineWidth = 2f,
                Orientation = Orientation.Vertical,
                Size = data.BarWidthsMm.Count > 0 ? data.BarWidthsMm[0] : 0.0,
            })
            .ToList();

        if (bars.Count > 0)
        {
            for (var i = 0; i < bars.Count && i < data.BarWidthsMm.Count; i++)
            {
                bars[i].Size = data.BarWidthsMm[i];
            }

            Plot.Add.Bars(bars);
            Plot.Axes.SetLimits(
                left: 0,
                right: data.MaxTravelMm,
                bottom: 0,
                top: yRangeTop);
        }

        AddStatistics(telemetryData, yRangeTop);
    }
}
