using System;
using System.Collections.Generic;
using ScottPlot;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

/// <summary>
/// Spring + damper force over time for one axle (Front or Rear). The damper signal is
/// plotted with a +trim offset so its zero aligns with the trim reference line.
/// </summary>
public class SpringDamperForceTimePlot(Plot plot, SuspensionType type) : TelemetryPlot(plot)
{
    private double[]? springForce;
    private double[]? damperForce;
    private double staticForce;

    private static readonly Color TrimColor = Color.FromHex("#FFD700");
    private static readonly Color SpringColor = Color.FromHex("#FFA500"); // orange
    private static readonly Color DamperColor = Color.FromHex("#FF00FF"); // magenta

    public void SetForceData(double staticForce, double[]? springForce, double[]? damperForce)
    {
        this.staticForce = staticForce;
        this.springForce = springForce;
        this.damperForce = damperForce;
    }

    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);
        var prefix = type == SuspensionType.Front ? "Front" : "Rear";
        SetTitle($"{prefix} spring + damper over time");
        Plot.Layout.Fixed(new PixelPadding(60, 14, 50, 40));
        Plot.Axes.Bottom.Label.Text = "Time (s)";
        Plot.Axes.Left.Label.Text = "Force (N)";

        int n = 0;
        if (springForce is not null) n = springForce.Length;
        if (damperForce is not null) n = n == 0 ? damperForce.Length : Math.Min(n, damperForce.Length);
        if (n <= 0) return;

        var sampleRate = telemetryData.SampleRate;
        if (sampleRate <= 0) return;
        var period = 1.0 / sampleRate;

        bool hasSpring = springForce is not null && springForce.Length >= n;
        bool hasDamper = damperForce is not null && damperForce.Length >= n;
        if (!hasSpring && !hasDamper) return;

        var seriesToDraw = new List<(double[] data, Color col)>();
        if (hasDamper)
        {
            var damperOffset = new double[n];
            for (int i = 0; i < n; i++) damperOffset[i] = damperForce![i] + staticForce;
            seriesToDraw.Add((damperOffset, DamperColor));
        }
        if (hasSpring) seriesToDraw.Add((springForce!, SpringColor));

        double yMaxData = double.NegativeInfinity;
        double yMinData = double.PositiveInfinity;
        foreach (var (arr, _) in seriesToDraw)
        {
            for (int i = 0; i < n; i++)
            {
                if (arr[i] > yMaxData) yMaxData = arr[i];
                if (arr[i] < yMinData) yMinData = arr[i];
            }
        }
        if (double.IsInfinity(yMaxData)) { yMaxData = 1; yMinData = 0; }

        if (staticForce > 0)
        {
            var trimLine = Plot.Add.HorizontalLine(staticForce);
            trimLine.Color = TrimColor;
            trimLine.LineStyle.Width = 2;
            trimLine.LineStyle.Pattern = LinePattern.Dashed;
            AddRefLineLabel("trim", staticForce, TrimColor);

            if (staticForce > yMaxData) yMaxData = staticForce;
            if (staticForce < yMinData) yMinData = staticForce;
        }

        foreach (var (arr, col) in seriesToDraw)
        {
            var sig = Plot.Add.Signal(arr, period);
            sig.Color = col;
            sig.LineWidth = 1;
        }

        var duration = n * period;
        var span = Math.Max(yMaxData - yMinData, 1.0);
        var yMin = yMinData - span * 0.05;
        var yMax = yMaxData + span * 0.05;
        Plot.Axes.SetLimits(left: 0, right: duration, bottom: yMin, top: yMax);

        var items = new List<LegendItem>();
        if (hasSpring) items.Add(new() { LabelText = "Spring", LabelFontColor = SpringColor, LabelBold = true });
        if (hasDamper) items.Add(new() { LabelText = "Damper", LabelFontColor = DamperColor, LabelBold = true });

        Plot.Legend.IsVisible = items.Count > 0;
        Plot.Legend.Alignment = Alignment.MiddleRight;
        Plot.Legend.BackgroundColor = Color.FromHex("#15191C").WithAlpha(220);
        Plot.Legend.OutlineColor = Color.FromHex("#505558");
        Plot.Legend.OutlineWidth = 1;
        Plot.Legend.FontSize = 10;
        Plot.Legend.Padding = new PixelPadding(6);
        Plot.Legend.SymbolWidth = 0;
        Plot.Legend.SymbolPadding = 0;
        Plot.Legend.DisplayPlottableLegendItems = false;
        Plot.Legend.ManualItems = items;
    }

    private void AddRefLineLabel(string content, double y, Color color)
    {
        var text = Plot.Add.Text(content, 0, y);
        text.LabelFontColor = color;
        text.LabelFontSize = 11;
        text.LabelBold = true;
        text.LabelAlignment = Alignment.LowerLeft;
        text.LabelOffsetX = 6;
        text.LabelOffsetY = -1;
    }
}
