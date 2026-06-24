using System;
using System.Collections.Generic;
using ScottPlot;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

public class ComparePositionVelocityPlot(Plot plot, SuspensionType type) : SufniPlot(plot)
{
    public void LoadMultipleSessions(List<(TelemetryData data, Color color, LinePattern pattern, string name)> sessions)
    {
        SetTitle(type == SuspensionType.Front
            ? "Fork position vs. velocity"
            : "Damper position vs velocity");
        Plot.Layout.Fixed(new PixelPadding(70, 14, 50, 40));

        var useDamperTravel = type == SuspensionType.Rear;
        Plot.Axes.Bottom.Label.Text = useDamperTravel ? "Damper travel (mm)" : "Fork travel (mm)";
        Plot.Axes.Left.Label.Text = useDamperTravel ? "Damper velocity (mm/s)" : "Fork velocity (mm/s)";

        var velocityMaxPositive = 0.0;
        var velocityMaxNegative = 0.0;
        var sharedMaxTravel = 0.0;

        // Cache each drawable session's phase-portrait path for pass 2 (avoid recomputing).
        var cached = new List<(Color color, PositionVelocityData data)>();

        // Pass 1: compute per-session data, track the global velocity extremes (positive and
        // |negative| independently) and the max travel across all sessions for shared limits.
        foreach (var (data, color, _, _) in sessions)
        {
            var suspension = type == SuspensionType.Front ? data.Front : data.Rear;
            if (!suspension.Present) continue;

            var maxStroke = type == SuspensionType.Front
                ? data.Linkage.MaxFrontStroke
                : data.Linkage.MaxRearStroke;
            if (maxStroke is null) continue;

            var pvData = type == SuspensionType.Front
                ? data.CalculateForkPositionVelocityData()
                : data.CalculateDamperPositionVelocityData();
            if (pvData.Travel.Length == 0) continue;

            if (maxStroke.Value > sharedMaxTravel) sharedMaxTravel = maxStroke.Value;

            foreach (var v in pvData.Velocity)
            {
                if (double.IsNaN(v)) continue;
                if (v > 0)
                    velocityMaxPositive = Math.Max(velocityMaxPositive, v);
                else
                    velocityMaxNegative = Math.Max(velocityMaxNegative, Math.Abs(v));
            }

            cached.Add((color, pvData));
        }

        // Pass 2: draw each session's phase portrait (NaN gaps handled by Scatter).
        foreach (var (color, pvData) in cached)
        {
            var scatter = Plot.Add.Scatter(pvData.Travel, pvData.Velocity);
            scatter.MarkerStyle.IsVisible = false;
            scatter.LineStyle.IsVisible = true;
            scatter.LineStyle.Width = 1;
            scatter.LineStyle.Color = color.WithOpacity(0.55);
        }

        // Add 10% padding and round up to nearest 500 independently, from the GLOBAL maxima.
        var topLimit = Math.Ceiling(velocityMaxPositive * 1.1 / 500.0) * 500.0;
        if (topLimit < 500.0) topLimit = 500.0;
        var bottomLimit = Math.Ceiling(velocityMaxNegative * 1.1 / 500.0) * 500.0;
        if (bottomLimit < 500.0) bottomLimit = 500.0;

        // Zero velocity reference line
        Plot.Add.HorizontalLine(0, 1f, Color.FromHex("#dddddd"), LinePattern.Dotted);

        Plot.Axes.SetLimits(
            left: 0,
            right: sharedMaxTravel,
            bottom: -bottomLimit,
            top: topLimit);

        // Legend = per-session colored names at the upper-right.
        var legendStep = topLimit * 0.08;
        for (var i = 0; i < sessions.Count; i++)
        {
            var (_, color, _, name) = sessions[i];
            var label = Plot.Add.Text(name, sharedMaxTravel, topLimit * 0.95 - i * legendStep);
            label.LabelFontColor = color;
            label.LabelFontSize = 12;
            label.LabelAlignment = Alignment.UpperRight;
            label.LabelOffsetX = -6;
        }
    }
}
