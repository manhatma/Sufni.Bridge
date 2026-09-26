using System;
using System.Collections.Generic;
using ScottPlot;
using Sufni.Bridge.Models;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Plots;

/// <summary>
/// Front/rear cross-spectrum diagnostics on a frequency axis: magnitude-squared coherence γ²(f)
/// on the left axis and the cross phase φ(f) on a second (right) axis. The body/pitch band
/// [1 Hz, fSplit] is marked with dotted verticals. A dashed horizontal line marks the 95 % noise
/// level of the coherence estimate, which depends on the number of Welch segments (short or cropped
/// sessions average few segments, and uncorrelated signals then reach high γ² by chance). Phase is
/// shown only where coherence exceeds both that level and a fixed readability floor. An anti-phase energy fraction summarises how much of the in-band modal
/// energy lives in the anti-phase (pitch) mode, de-lagged by the front→rear traversal lag τ where
/// determinable; where τ is not determinable this is an upper bound on the true chassis-pitch
/// energy fraction.
/// </summary>
public class PitchCoherencePlot(Plot plot, Discipline? discipline) : TelemetryPlot(plot)
{
    private const double DisplayMaxHz = 6.0;
    private const double PhaseCoherenceFloor = 0.3;   // below this, cross phase is wrapping noise
    private static readonly Color NoiseLevelColor = Color.FromHex("#888888");
    private static readonly Color PhaseColor = Color.FromHex("#F4511E");

    public override void LoadTelemetryData(TelemetryData telemetryData)
    {
        base.LoadTelemetryData(telemetryData);

        SetTitle("Front/rear coherence & phase");
        Plot.Layout.Fixed(new PixelPadding(55, 62, 50, 40));
        Plot.Axes.Bottom.Label.Text = "Frequency (Hz)";
        Plot.Axes.Left.Label.Text = "Coherence γ²";

        var modal = telemetryData.CalculateModalSpectrum();
        if (modal is null)
        {
            AddLabel("Coherence needs front and rear travel", 0.5, 0.5, 0, 0, Alignment.MiddleCenter, "#aaaaaa");
            return;
        }

        var (freqs, coherence, phaseDeg, pitchPsd, heavePsd) = modal.Value;

        var fSplit = TelemetryData.FrequencySplitFor(discipline);

        // Restrict the displayed series to the 0..DisplayMaxHz window (keep the full arrays for the energy integral).
        var dispF = new List<double>(freqs.Length);
        var dispCoh = new List<double>(freqs.Length);
        var dispPhase = new List<double>(freqs.Length);
        for (var k = 0; k < freqs.Length; k++)
        {
            // Skip DC: after per-segment mean removal the 0 Hz bin carries no front/rear relation.
            if (freqs[k] <= 0.0 || freqs[k] > DisplayMaxHz) continue;
            dispF.Add(freqs[k]);
            dispCoh.Add(coherence[k]);
            dispPhase.Add(phaseDeg[k]);
        }

        if (dispF.Count == 0)
        {
            AddLabel("No spectral data in display range", 0.5, 0.5, 0, 0, Alignment.MiddleCenter, "#aaaaaa");
            return;
        }

        // LEFT axis: coherence γ² in [0,1], drawn as a line (markers off) in the front colour.
        var cohLine = Plot.Add.Scatter(dispF.ToArray(), dispCoh.ToArray());
        cohLine.MarkerStyle.IsVisible = false;
        cohLine.LineStyle.Color = FrontColor;
        cohLine.LineStyle.Width = 1.5f;

        // RIGHT axis: phase in degrees, range [-180,180]. ScottPlot 5 dual axis: assign the
        // scatter's YAxis to Plot.Axes.Right.
        var rightAxis = Plot.Axes.Right;
        rightAxis.Label.Text = "Phase (°)";
        // ScottPlot's RightAxis defaults its label to Rotation = 90 (reads top-to-bottom); flip it
        // 180° to -90 so "Phase (°)" reads bottom-to-top, matching the left axis and the conventional
        // orientation. Only the label orientation changes; phase data is untouched.
        rightAxis.Label.Rotation = -90f;
        rightAxis.Label.ForeColor = Color.FromHex("#D0D0D0");
        rightAxis.Label.FontSize = 14;
        // Match the app's other axis labels: SufniPlot only un-bolds Left/Bottom, so the right axis
        // keeps ScottPlot's bold default — clear it. Nudge inward so it isn't jammed against the edge.
        rightAxis.Label.Bold = false;
        rightAxis.Label.OffsetX = -10;
        rightAxis.TickLabelStyle.ForeColor = Color.FromHex("#D0D0D0");
        rightAxis.TickLabelStyle.FontSize = 12;

        // 95 % noise level of γ² for this session's segment count: coherence below the line is
        // indistinguishable from two unrelated signals.
        var noiseLevel = telemetryData.CoherenceSignificanceLevel();
        if (noiseLevel < 1.0)
        {
            Plot.Add.HorizontalLine(noiseLevel, 1f, NoiseLevelColor, LinePattern.Dashed);
            var noiseLabel = Plot.Add.Text("95 % noise", DisplayMaxHz, noiseLevel);
            noiseLabel.LabelFontSize = 9;
            noiseLabel.LabelFontColor = Color.FromHex("#AAAAAA");
            noiseLabel.LabelAlignment = Alignment.LowerRight;
            noiseLabel.LabelOffsetX = -4;
            noiseLabel.LabelOffsetY = -2;
        }

        // Phase only where coherence is significant and high enough to be readable; elsewhere the
        // cross phase is just ±180° wrapping noise. Drawn as dots so no false connectors bridge the
        // masked gaps.
        var phaseFloor = Math.Max(PhaseCoherenceFloor, noiseLevel);
        var phaseF = new List<double>();
        var phaseP = new List<double>();
        for (var k = 0; k < dispF.Count; k++)
        {
            if (dispCoh[k] < phaseFloor) continue;
            phaseF.Add(dispF[k]);
            phaseP.Add(dispPhase[k]);
        }
        if (phaseF.Count > 0)
        {
            var phaseDots = Plot.Add.Scatter(phaseF.ToArray(), phaseP.ToArray());
            phaseDots.LineStyle.IsVisible = false;
            phaseDots.MarkerStyle.IsVisible = true;
            phaseDots.MarkerStyle.Size = 4f;
            phaseDots.MarkerStyle.FillColor = PhaseColor;
            phaseDots.MarkerStyle.LineColor = PhaseColor;
            phaseDots.Axes.YAxis = rightAxis;
        }

        // Body/pitch band markers: faint dotted verticals at 1 Hz and fSplit.
        Plot.Add.VerticalLine(1.0, 1f, Color.FromHex("#888888"), LinePattern.Dotted);
        Plot.Add.VerticalLine(fSplit, 1f, Color.FromHex("#888888"), LinePattern.Dotted);

        // The front→rear traversal lag τ is heave-dominated and rarely determinable on real trails,
        // so no lag-model line / τ annotation is drawn here (it would mislead more than inform). τ is
        // still used internally for the pitch de-lag where it IS determinable. The measured coherence
        // and phase above are the honest content of this plot.

        // Anti-phase energy fraction over [1, fSplit], using the FULL (unclipped) spectrum arrays.
        var pitchSum = new double[freqs.Length];
        for (var k = 0; k < freqs.Length; k++)
            pitchSum[k] = pitchPsd[k] + heavePsd[k];
        var ep = TelemetryData.IntegrateBand(freqs, pitchPsd, 1.0, fSplit);
        var et = TelemetryData.IntegrateBand(freqs, pitchSum, 1.0, fSplit);
        var frac = et > 0 ? ep / et : 0.0;
        // Info box (top-right, over the low-coherence/empty region): anti-phase energy fraction.
        // Few Welch segments (noise level above the readability floor, i.e. fewer than ~10) make
        // the cross-spectrum and so the modal split unreliable; say so instead of implying precision.
        var fewSegments = noiseLevel > PhaseCoherenceFloor;
        var info = Plot.Add.Text(
            $"anti-phase energy: {frac:0.00}{(fewSegments ? " (short, low confidence)" : "")}", DisplayMaxHz, 1);
        info.LabelFontColor = Color.FromHex("#FFD700");
        info.LabelFontSize = 9;
        info.LabelFontName = "Menlo";
        info.LabelAlignment = Alignment.UpperRight;
        info.LabelOffsetX = -8;
        info.LabelOffsetY = 8;
        info.LabelBold = true;
        info.LabelBackgroundColor = Color.FromHex("#15191C").WithAlpha(220);
        info.LabelBorderColor = Color.FromHex("#FFD700").WithAlpha(80);
        info.LabelBorderWidth = 1;
        info.LabelPadding = 4;

        // Axis limits: X 0..DisplayMaxHz, left Y 0..1, right Y -180..180.
        Plot.Axes.SetLimits(left: 0, right: DisplayMaxHz, bottom: 0, top: 1);
        rightAxis.Min = -180;
        rightAxis.Max = 180;
    }
}
