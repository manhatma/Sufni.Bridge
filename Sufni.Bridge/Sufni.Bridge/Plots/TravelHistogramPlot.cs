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
        text.LabelAlignment = alignment;
        text.LabelOffsetX = xoffset;
        text.LabelOffsetY = yoffset;
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

        Plot.Add.VerticalLine(avgPercentage, 2f, StatColor, LinePattern.Dashed);
        Plot.Add.VerticalLine(maxPercentage, 2f, StatColor, LinePattern.Dashed);
        Plot.Add.VerticalLine(p95Percentage, 2f, StatColor, LinePattern.Dashed);

        AddSmallLabel("avg", avgPercentage, yRangeTop * 0.95, -8, 0, Alignment.MiddleRight);
        AddSmallLabel("max", maxPercentage, yRangeTop * 0.95, -8, 0, Alignment.MiddleRight);
        AddSmallLabel("95th", p95Percentage, yRangeTop * 0.95, -8, 0, Alignment.MiddleRight);

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

        var statsLabel = Plot.Add.Text(statsText, 100.0, yRangeTop * 0.85);
        statsLabel.LabelFontColor = StatColor;
        statsLabel.LabelFontSize = 10;
        statsLabel.LabelFontName = "Menlo";
        statsLabel.LabelAlignment = Alignment.UpperRight;
        statsLabel.LabelOffsetX = -10; // 1em margin between label right edge and axis
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
        Plot.Layout.Fixed(new PixelPadding(70, 20, 50, 40));

        Plot.Axes.Bottom.Label.Text = "Travel (%)";
        Plot.Axes.Left.Label.Text = "Time (%)";

        var data = telemetryData.CalculateDetailedTravelHistogram(type);
        var maxTime = data.TimePercentage.Count > 0 ? data.TimePercentage.Max() : 1.0;
        var yRangeTop = Math.Max(1.0, maxTime) * HistogramRangeMultiplier;
        var color = type == SuspensionType.Front ? FrontColor : RearColor;

        var bars = data.TravelMidsPercentage.Zip(data.TimePercentage)
            .Select(tuple => new Bar
            {
                Position = tuple.First,
                Value = tuple.Second,
                FillColor = color.WithOpacity(),
                LineColor = color,
                LineWidth = 2f,
                Orientation = Orientation.Vertical,
                Size = data.BarWidthsPercentage.Count > 0 ? data.BarWidthsPercentage[0] : 0.0,
            })
            .ToList();

        if (bars.Count > 0)
        {
            for (var i = 0; i < bars.Count && i < data.BarWidthsPercentage.Count; i++)
            {
                bars[i].Size = data.BarWidthsPercentage[i];
            }

            Plot.Add.Bars(bars);
            Plot.Axes.SetLimits(left: 0, right: 100, bottom: 0, top: yRangeTop);
        }

        Plot.Axes.Bottom.TickGenerator = new NumericManual(
            [0.0, 10.0, 20.0, 30.0, 40.0, 50.0, 60.0, 70.0, 80.0, 90.0, 100.0],
            ["0", "10", "20", "30", "40", "50", "60", "70", "80", "90", "100"]);

        AddStatistics(telemetryData, yRangeTop);
    }
}