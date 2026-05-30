using System;
using System.Linq;
using ScottPlot;
using ScottPlot.TickGenerators;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

public class TravelHistogramPlot(Plot plot, SuspensionType type) : TelemetryPlot(plot)
{
    private const double HistogramRangeMultiplier = 1.3;

    private static readonly Color StatColor = Color.FromHex("#FFD700");

    private void AddSmallLabel(string content, double x, double y, int xoffset, int yoffset, Alignment alignment)
    {
        var text = Plot.Add.Text(content, x, y);
        text.LabelFontColor = StatColor;
        text.LabelFontSize = 12;
        text.LabelAlignment = Alignment.LowerCenter;
        text.LabelRotation = -90;
        text.LabelOffsetX = 0;
        text.LabelOffsetY = 4;
    }

    private void AddStatistics(TelemetryData telemetryData, double yRangeTop)
    {
        var statistics = telemetryData.CalculateDetailedTravelStatistics(type);

        var mx = type == SuspensionType.Front
            ? telemetryData.Linkage.MaxFrontTravel
            : telemetryData.Linkage.MaxRearTravel;
        var avgPercentage = mx > 0 ? statistics.Average / mx * 100.0 : 0.0;
        var maxPercentage = mx > 0 ? statistics.Max / mx * 100.0 : 0.0;
        var p95Percentage = mx > 0 ? statistics.P95 / mx * 100.0 : 0.0;

        Plot.Add.VerticalLine(statistics.Average, 2f, StatColor, LinePattern.Dashed);
        Plot.Add.VerticalLine(statistics.Max, 2f, StatColor, LinePattern.Dashed);
        Plot.Add.VerticalLine(statistics.P95, 2f, StatColor, LinePattern.Dashed);

        AddSmallLabel("avg", statistics.Average, yRangeTop * 0.95, -8, 0, Alignment.MiddleRight);
        AddSmallLabel("max", statistics.Max, yRangeTop * 0.95, -8, 0, Alignment.MiddleRight);
        AddSmallLabel("95th", statistics.P95, yRangeTop * 0.95, -8, 0, Alignment.MiddleRight);

        // Tabular layout with monospace font — use non-breaking spaces (\u00A0) to prevent
        // SVG whitespace normalization from collapsing leading padding spaces
        static string N(double val, int width = 5) =>
            val.ToString("F1").PadLeft(width).Replace(' ', '\u00A0');

        // Main lines are 22 chars wide; pad last line with trailing NBSPs so it left-aligns
        // visually in the right-aligned text box (ScottPlot right-aligns each line to anchor)
        const int lineWidth = 22;
        var boLine = $"Bottom\u00A0outs:\u00A0{statistics.Bottomouts}";
        var statsText =
            $"Avg:\u00A0\u00A0{N(statistics.Average)}\u00A0mm\u00A0({N(avgPercentage, 4)}%)\n" +
            $"95th:\u00A0{N(statistics.P95)}\u00A0mm\u00A0({N(p95Percentage, 4)}%)\n" +
            $"Max:\u00A0\u00A0{N(statistics.Max)}\u00A0mm\u00A0({N(maxPercentage, 4)}%)\n" +
            boLine.PadRight(lineWidth, '\u00A0');

        var statsLabel = Plot.Add.Text(statsText, mx, yRangeTop * 0.85);
        statsLabel.LabelFontColor = StatColor;
        statsLabel.LabelFontSize = 9;
        statsLabel.LabelFontName = "Menlo";
        statsLabel.LabelAlignment = Alignment.UpperRight;
        statsLabel.LabelOffsetX = -10; // 1em margin between label right edge and axis
        statsLabel.LabelOffsetY = 3;
        statsLabel.LabelBold = true;
        statsLabel.LabelBackgroundColor = Color.FromHex("#15191C").WithAlpha(220);
        statsLabel.LabelBorderColor = StatColor.WithAlpha(80);
        statsLabel.LabelBorderWidth = 1;
        statsLabel.LabelPadding = 5;
    }

    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);

        SetTitle(type == SuspensionType.Front
            ? "Front travel histogram"
            : "Rear travel histogram");
        Plot.Layout.Fixed(new PixelPadding(50, 24, 50, 40));

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
            Plot.Axes.SetLimits(left: 0, right: data.MaxTravelMm, bottom: 0, top: yRangeTop);
        }

        AddStatistics(telemetryData, yRangeTop);
    }
}