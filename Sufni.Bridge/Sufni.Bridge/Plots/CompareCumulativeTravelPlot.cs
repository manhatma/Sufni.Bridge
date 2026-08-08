using System;
using System.Collections.Generic;
using System.Globalization;
using ScottPlot;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

public class CompareCumulativeTravelPlot(Plot plot) : SufniPlot(plot)
{
    private const double MinTotalForShareMm = 500.0;
    private const int DecimatedPointCount = 1200;

    private static (double[] xs, double[] ys) DecimateForPlot(double[] cumulativeMm, double period)
    {
        var n = cumulativeMm.Length;
        var m = Math.Min(DecimatedPointCount, n);
        var xs = new double[m];
        var ys = new double[m];

        for (var j = 0; j < m; j++)
        {
            var idx = m == 1 ? 0 : (long)j * (n - 1) / (m - 1);
            xs[j] = idx * period;
            ys[j] = cumulativeMm[idx] / 1000.0;
        }

        return (xs, ys);
    }

    public void LoadMultipleSessions(List<(TelemetryData data, Color color, LinePattern pattern, string name)> sessions)
    {
        SetTitle("Cumulative travel");
        Plot.Layout.Fixed(new PixelPadding(55, 24, 50, 40));
        Plot.Axes.Bottom.Label.Text = "Time (s)";
        Plot.Axes.Left.Label.Text = "Cumulative travel (m)";

        var labels = new List<(Color color, string text)>();
        var maxDuration = 0.0;
        var maxTravelM = 0.0;

        foreach (var (data, color, _, name) in sessions)
        {
            var period = 1.0 / data.SampleRate;
            var durationSamples = 0;
            var frontTotalMm = 0.0;
            var rearTotalMm = 0.0;
            var hasFront = false;
            var hasRear = false;

            if (data.Front.Present)
            {
                var cumulativeMm = data.CalculateCumulativeTravel(SuspensionType.Front);
                if (cumulativeMm.Length > 0)
                {
                    // Decimate drawing data because Signal's per-pixel-column rendering swallows dash patterns.
                    var (xs, ys) = DecimateForPlot(cumulativeMm, period);
                    var scatter = Plot.Add.ScatterLine(xs, ys);
                    scatter.LineStyle.Color = color;
                    scatter.LineStyle.Width = 1;
                    scatter.LineStyle.Pattern = LinePattern.Solid;
                    frontTotalMm = cumulativeMm[^1];
                    hasFront = true;
                    durationSamples = Math.Max(durationSamples, cumulativeMm.Length);
                    maxTravelM = Math.Max(maxTravelM, cumulativeMm[^1] / 1000.0);
                }
            }

            if (data.Rear.Present)
            {
                var cumulativeMm = data.CalculateCumulativeTravel(SuspensionType.Rear);
                if (cumulativeMm.Length > 0)
                {
                    var (xs, ys) = DecimateForPlot(cumulativeMm, period);
                    var scatter = Plot.Add.ScatterLine(xs, ys);
                    scatter.LineStyle.Color = color;
                    scatter.LineStyle.Width = 1;
                    scatter.LineStyle.Pattern = LinePattern.Dashed;
                    rearTotalMm = cumulativeMm[^1];
                    hasRear = true;
                    durationSamples = Math.Max(durationSamples, cumulativeMm.Length);
                    maxTravelM = Math.Max(maxTravelM, cumulativeMm[^1] / 1000.0);
                }
            }

            if (durationSamples == 0) continue;

            var durationSeconds = durationSamples * period;
            var totalMm = frontTotalMm + rearTotalMm;
            var frontShare = totalMm < MinTotalForShareMm ? double.NaN : frontTotalMm / totalMm * 100.0;
            var shareText = double.IsNaN(frontShare)
                ? "—"
                : frontShare.ToString("0.0", CultureInfo.InvariantCulture);
            var frontText = hasFront
                ? (frontTotalMm / 1000.0).ToString("0", CultureInfo.InvariantCulture)
                : "—";
            var rearText = hasRear
                ? (rearTotalMm / 1000.0).ToString("0", CultureInfo.InvariantCulture)
                : "—";
            labels.Add((color, $"{name}: F {frontText} m / R {rearText} m · {shareText}%"));
            maxDuration = Math.Max(maxDuration, durationSeconds);
        }

        if (maxDuration <= 0 || maxTravelM <= 0)
        {
            AddLabel("Cumulative travel data is unavailable", 0.5, 0.5, 0, 0,
                Alignment.MiddleCenter, "#aaaaaa");
            return;
        }

        var yTop = maxTravelM * 1.05;
        Plot.Axes.SetLimitsX(0, maxDuration);
        Plot.Axes.SetLimitsY(0, yTop);

        for (var i = 0; i < labels.Count; i++)
        {
            var (color, text) = labels[i];
            var label = Plot.Add.Text(text, maxDuration * 0.02, yTop * 0.96);
            label.LabelFontColor = color;
            label.LabelFontSize = 9;
            label.LabelFontName = "Menlo";
            label.LabelAlignment = Alignment.UpperLeft;
            label.LabelOffsetX = 4;
            label.LabelOffsetY = i * 18;
            label.LabelBold = true;
            label.LabelBackgroundColor = Color.FromHex("#15191C").WithAlpha(220);
            label.LabelBorderColor = color.WithAlpha(80);
            label.LabelBorderWidth = 1;
            label.LabelPadding = 4;
        }

        // Combined front/rear hint line below the session labels
        var hint = Plot.Add.Text("— Front   - - Rear", maxDuration * 0.02, yTop * 0.96);
        hint.LabelFontColor = Color.FromHex("#808080");
        hint.LabelFontSize = 10;
        hint.LabelAlignment = Alignment.UpperLeft;
        hint.LabelOffsetX = 4;
        hint.LabelOffsetY = labels.Count * 18 + 2;
    }
}
