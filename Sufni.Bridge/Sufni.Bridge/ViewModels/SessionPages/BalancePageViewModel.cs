using System;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Svg.Skia;
using CommunityToolkit.Mvvm.ComponentModel;
using Sufni.Bridge.Models;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.ViewModels.SessionPages;

public enum BalanceStatus { Unknown, Good, Acceptable, Critical }

public partial class BalanceMetricRow : ObservableObject
{
    [ObservableProperty] private string label = "";
    [ObservableProperty] private string value = "—";
    [ObservableProperty] private string target = "";
    [ObservableProperty] private BalanceStatus status = BalanceStatus.Unknown;

    public IBrush ValueBrush => Status switch
    {
        BalanceStatus.Good       => new SolidColorBrush(Color.FromRgb(0x6C, 0xC4, 0x4A)),
        BalanceStatus.Acceptable => new SolidColorBrush(Color.FromRgb(0xE0, 0xB8, 0x3A)),
        BalanceStatus.Critical   => new SolidColorBrush(Color.FromRgb(0xE0, 0x6A, 0x55)),
        _                        => new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0)),
    };

    partial void OnStatusChanged(BalanceStatus value) => OnPropertyChanged(nameof(ValueBrush));
}

public partial class BalanceMetricsViewModel : ObservableObject
{
    public BalanceMetricRow FrontSag      { get; } = new() { Label = "Front SAG (dyn.)", Target = "23–28 %" };
    public BalanceMetricRow RearSag       { get; } = new() { Label = "Rear SAG (dyn.)",  Target = "28–33 %" };
    public BalanceMetricRow SagDiff       { get; } = new() { Label = "Sag-Diff |F−R|",   Target = "≤ 5 pp" };
    public BalanceMetricRow FrontP95      { get; } = new() { Label = "Front 95th",       Target = "> 55 %" };
    public BalanceMetricRow RearP95       { get; } = new() { Label = "Rear 95th",        Target = "> 55 %" };
    public BalanceMetricRow P95Diff       { get; } = new() { Label = "95th-Diff |F−R|",  Target = "≤ 5 pp" };
    public BalanceMetricRow FrontBO       { get; } = new() { Label = "Front Bottom-out", Target = "≈ 0" };
    public BalanceMetricRow RearBO        { get; } = new() { Label = "Rear Bottom-out",  Target = "≈ 0" };
    public BalanceMetricRow CompVelRatio  { get; } = new() { Label = "Comp Vel F/R",     Target = "−0.08 … +0.07" };
    public BalanceMetricRow RebVelRatio   { get; } = new() { Label = "Reb Vel F/R",      Target = "0.00 … +0.07" };
    public BalanceMetricRow CompMsd       { get; } = new() { Label = "MSD Compression",  Target = "≈ 0" };
    public BalanceMetricRow RebMsd        { get; } = new() { Label = "MSD Rebound",      Target = "−10 to 0 %" };
    // Discipline-dependent eigenfrequency targets — see GetFreqBands below.
    // Following Vorsprung's recommendation, the band starts equal front/rear
    // (lower bound matches Rear); the upper bound runs ~0.3 Hz higher to allow
    // for the common practice of running the front slightly stiffer.
    // Defaults are Enduro; updated when Apply runs.
    public BalanceMetricRow FrontFreq     { get; } = new() { Label = "Front Eigenfreq.", Target = "2.1–3.2 Hz" };
    public BalanceMetricRow RearFreq      { get; } = new() { Label = "Rear Eigenfreq.",  Target = "2.1–2.9 Hz" };
    public BalanceMetricRow FreqDiff      { get; } = new() { Label = "Frequency-Diff |F−R|", Target = "≤ 0.4 Hz" };
    public BalanceMetricRow PeakAmpRatio  { get; } = new() { Label = "Peak Amp F/R",     Target = "−0.05 … +0.05" };

    // Front/Rear travel-energy distribution per band (10·log10(F/R)).
    // Low/Mid split is discipline-aware (see TelemetryData.FrequencySplitFor);
    // Wheel band fix 10–25 Hz (unsprung resonance), High fix 25–50 Hz (above-resonance noise).
    public BalanceMetricRow LowEnergyRatio   { get; } = new() { Label = "Energy F/R Low",   Target = "0 dB ±2" };
    public BalanceMetricRow MidEnergyRatio   { get; } = new() { Label = "Energy F/R Mid",   Target = "0 dB ±2" };
    public BalanceMetricRow WheelEnergyRatio { get; } = new() { Label = "Energy F/R (10.0–25.0 Hz)", Target = "0 dB ±2" };
    public BalanceMetricRow HighEnergyRatio  { get; } = new() { Label = "Energy F/R (25.0–50.0 Hz)", Target = "0 dB ±2" };

    // Front/Rear coupling per band (mean magnitude-squared coherence γ²).
    // Low band: high coherence is desirable (frame couples both axes during pitch/heave).
    // Mid/Wheel/High: low coherence is desirable — surface- and tire-driven, axes
    // should be excited independently. High γ² there implies shared excitation
    // (drivetrain bob, frame resonance), which is generally not what you want.
    public BalanceMetricRow LowCoherence   { get; } = new() { Label = "Coherence Low",   Target = "≥ 0.7" };
    public BalanceMetricRow MidCoherence   { get; } = new() { Label = "Coherence Mid",   Target = "≤ 0.4" };
    public BalanceMetricRow WheelCoherence { get; } = new() { Label = "Coherence (10.0–25.0 Hz)", Target = "≤ 0.4" };
    public BalanceMetricRow HighCoherence  { get; } = new() { Label = "Coherence (25.0–50.0 Hz)", Target = "≤ 0.1" };

    // Dynamic wheel-load metrics. Active only when the session has a spring component
    // assigned and a parsable spring-rate value. Source: spring-curve-based force estimator.
    public BalanceMetricRow FrontStaticForce { get; } = new() { Label = "Front static load",     Target = "" };
    public BalanceMetricRow RearStaticForce  { get; } = new() { Label = "Rear static load",      Target = "" };
    public BalanceMetricRow FrontUnloading   { get; } = new() { Label = "Front unloading time",  Target = "≤ 5 %" };
    public BalanceMetricRow RearUnloading    { get; } = new() { Label = "Rear unloading time",   Target = "≤ 5 %" };
    public BalanceMetricRow UnloadingDiff    { get; } = new() { Label = "Unloading-Diff |F−R|",  Target = "≤ 5 pp" };
    public BalanceMetricRow FrontUnloadEv    { get; } = new() { Label = "Front unload events",   Target = "≈ 0" };
    public BalanceMetricRow RearUnloadEv     { get; } = new() { Label = "Rear unload events",    Target = "≈ 0" };
    public BalanceMetricRow FrontDynRange    { get; } = new() { Label = "Front dynamic range",   Target = "≈ 2.0–7.0×" };
    public BalanceMetricRow RearDynRange     { get; } = new() { Label = "Rear dynamic range",    Target = "≈ 2.0–7.0×" };
    public BalanceMetricRow DynRangeDiff     { get; } = new() { Label = "DynRange-Diff |F−R|",   Target = "≤ 0.5×" };
    // Statically-normalised Michelson load index per frequency band, range [-1, +1].
    // 0 = both axes oscillate equally relative to their static load. Positive = front-biased.
    // Low/Mid bands favour a slight front bias (front grip preferred for braking and
    // tracking). Wheel/High are tied to unsprung-mass dynamics, which aren't axis-biased,
    // so those bands stay symmetric.
    public BalanceMetricRow LowLoadMichelson   { get; } = new() { Label = "Load F/R Low",   Target = "−0.02 … +0.10" };
    public BalanceMetricRow MidLoadMichelson   { get; } = new() { Label = "Load F/R Mid",   Target = "−0.02 … +0.10" };
    public BalanceMetricRow WheelLoadMichelson { get; } = new() { Label = "Load F/R (10.0–25.0 Hz)", Target = "±0.05" };
    public BalanceMetricRow HighLoadMichelson  { get; } = new() { Label = "Load F/R (25.0–50.0 Hz)", Target = "±0.05" };

    public void Apply(BalanceMetrics m, Discipline? discipline = null)
    {
        SetSagBand(FrontSag, m.FrontSagPct, 23, 28);
        SetSagBand(RearSag,  m.RearSagPct,  28, 33);
        SetThreshold(SagDiff, m.SagDifferencePp, "{0:0.0} pp", 5.0, 8.0, lowerIsBetter: true);
        SetThreshold(FrontP95, m.FrontP95Pct, "{0:0.0} %", 55.0, 50.0, lowerIsBetter: false);
        SetThreshold(RearP95,  m.RearP95Pct,  "{0:0.0} %", 55.0, 50.0, lowerIsBetter: false);
        var p95Diff = (m.FrontP95Pct.HasValue && m.RearP95Pct.HasValue)
            ? (double?)Math.Abs(m.FrontP95Pct.Value - m.RearP95Pct.Value)
            : null;
        SetThreshold(P95Diff, p95Diff, "{0:0.0} pp", 5.0, 10.0, lowerIsBetter: true);
        SetCount(FrontBO, m.FrontBottomouts);
        SetCount(RearBO,  m.RearBottomouts);
        // Michelson index (F-R)/(F+R): bands derived from old ratio bands 0.85–1.15 (good)
        // and 0.80–1.20 (acceptable), via x → (x-1)/(x+1).
        SetSignedBand(CompVelRatio, m.CompressionVelocityRatio, -0.0811, 0.0698, -0.1111, 0.0909);
        // Rebound: rear should NOT rebound faster than front (kicks under jumps).
        // F/R < 1 means rear is faster → push the band upward.
        // Asymmetric: front rebound should not be slower than rear (rear-faster rebound = kick).
        // Acceptable band stays at 1.00–1.20 → 0.0–0.0909 in Michelson space.
        SetSignedBand(RebVelRatio,  m.ReboundVelocityRatio,      0.0,    0.0698,  0.0,    0.0909);
        SetMsd(CompMsd, m.CompressionMsd);
        SetMsdRebound(RebMsd, m.ReboundMsd);
        var (frontLo, frontHi, rearLo, rearHi) = GetFreqBands(discipline ?? Discipline.Enduro);
        FrontFreq.Target = string.Format(CultureInfo.InvariantCulture, "{0:0.0}–{1:0.0} Hz", frontLo, frontHi);
        RearFreq.Target  = string.Format(CultureInfo.InvariantCulture, "{0:0.0}–{1:0.0} Hz", rearLo,  rearHi);
        SetFreqBand(FrontFreq, m.FrontPeakFrequencyHz, frontLo, frontHi);
        SetFreqBand(RearFreq,  m.RearPeakFrequencyHz,  rearLo,  rearHi);
        SetFreqDiff(FreqDiff, m.FrequencyDifferenceHz);
        // Michelson bands derived from old ratio bands 0.9–1.1 (good) and 0.8–1.2 (acceptable).
        SetSignedBand(PeakAmpRatio, m.PeakAmplitudeRatio, -0.0526, 0.0476, -0.1111, 0.0909);

        var fSplit = m.FrequencySplitHz ?? 2.0;
        var fSplitStr = fSplit.ToString("0.0", CultureInfo.InvariantCulture);
        LowEnergyRatio.Label   = $"Energy F/R (1.0–{fSplitStr} Hz)";
        MidEnergyRatio.Label   = $"Energy F/R ({fSplitStr}–10.0 Hz)";
        WheelEnergyRatio.Label = "Energy F/R (10.0–25.0 Hz)";
        HighEnergyRatio.Label  = "Energy F/R (25.0–50.0 Hz)";
        SetEnergyRatioDb(LowEnergyRatio,   m.LowEnergyRatioDb);
        SetEnergyRatioDb(MidEnergyRatio,   m.MidEnergyRatioDb);
        SetEnergyRatioDb(WheelEnergyRatio, m.WheelEnergyRatioDb);
        SetEnergyRatioDb(HighEnergyRatio,  m.HighEnergyRatioDb);

        LowCoherence.Label   = $"Coherence (1.0–{fSplitStr} Hz)";
        MidCoherence.Label   = $"Coherence ({fSplitStr}–10.0 Hz)";
        WheelCoherence.Label = "Coherence (10.0–25.0 Hz)";
        HighCoherence.Label  = "Coherence (25.0–50.0 Hz)";
        SetCoherence(LowCoherence,   m.LowCoherence,   higherIsBetter: true);
        SetCoherence(MidCoherence,   m.MidCoherence,   higherIsBetter: false);
        SetCoherence(WheelCoherence, m.WheelCoherence, higherIsBetter: false);
        SetCoherence(HighCoherence,  m.HighCoherence,  higherIsBetter: false, goodCutoff: 0.1);

        // Wheel-load section. Static load is informational only (no status); the rest
        // reuses existing helpers — SetThreshold for one-sided %-pp metrics, SetCount
        // for unload events, SetSignedBand (Michelson) for the per-band load ratio.
        LowLoadMichelson.Label   = $"Load F/R (1.0–{fSplitStr} Hz)";
        MidLoadMichelson.Label   = $"Load F/R ({fSplitStr}–10.0 Hz)";
        WheelLoadMichelson.Label = "Load F/R (10.0–25.0 Hz)";
        HighLoadMichelson.Label  = "Load F/R (25.0–50.0 Hz)";

        // Static loads: show absolute Newton plus share of the dynamic total. The N value
        // is the median of F_wheel(t), reflecting the *dynamic* (trail) sag. We compare
        // its share against a bike-specific flat-floor reference computed from the spring
        // setup at the centre of the sag-target band (front 25.5 %, rear 30.5 %). That gives
        // a personalised expected share instead of a generic 35/45 % range.
        SetForceWithShare(FrontStaticForce, m.FrontStaticForceN,
            m.FrontStaticForceN, m.RearStaticForceN,
            m.FrontStaticForceFlatN, m.RearStaticForceFlatN, isFront: true);
        SetForceWithShare(RearStaticForce, m.RearStaticForceN,
            m.FrontStaticForceN, m.RearStaticForceN,
            m.FrontStaticForceFlatN, m.RearStaticForceFlatN, isFront: false);
        SetThreshold(FrontUnloading, m.FrontUnloadingPct, "{0:0.0} %", 5.0, 15.0, lowerIsBetter: true);
        SetThreshold(RearUnloading,  m.RearUnloadingPct,  "{0:0.0} %", 5.0, 15.0, lowerIsBetter: true);
        SetThreshold(UnloadingDiff,  m.UnloadingDifferencePp, "{0:0.0} pp", 5.0, 10.0, lowerIsBetter: true);
        SetCount(FrontUnloadEv, m.FrontUnloadingEvents);
        SetCount(RearUnloadEv,  m.RearUnloadingEvents);
        // Per-axis dynamic range as a load-multiplier (max−min)/static. 2.0–7.0× is the
        // typical band on real trails; 1.0–8.0× stays acceptable; outside that the suspension
        // is either barely moving or constantly bottoming/topping out.
        SetRangeBand(FrontDynRange, m.FrontDynamicRangeFactor, "{0:0.00}×", 2.0, 7.0, 1.0, 8.0);
        SetRangeBand(RearDynRange,  m.RearDynamicRangeFactor,  "{0:0.00}×", 2.0, 7.0, 1.0, 8.0);
        // F-R difference of the dynamic-range factors. ≤ 0.5× balanced, ≤ 1.0× acceptable.
        // Above that, one axle swings noticeably more than the other (setup mismatch).
        SetThreshold(DynRangeDiff, m.DynamicRangeDifference, "{0:0.00}×", 0.5, 1.0, lowerIsBetter: true);
        // Asymmetric Michelson bands for low/mid: a slight positive (front-biased) value is
        // desirable — front grip aids braking and line-tracking. Wheel/High remain symmetric:
        // unsprung-mass dynamics are tied to tire/rim properties, not to which axle they're on.
        SetSignedBand(LowLoadMichelson,   m.LowLoadMichelson,   -0.02, 0.10, -0.08, 0.20);
        SetSignedBand(MidLoadMichelson,   m.MidLoadMichelson,   -0.02, 0.10, -0.08, 0.15);
        SetSignedBand(WheelLoadMichelson, m.WheelLoadMichelson, -0.05, 0.05, -0.10, 0.10);
        SetSignedBand(HighLoadMichelson,  m.HighLoadMichelson,  -0.05, 0.05, -0.10, 0.10);
    }

    /// <summary>
    /// Format a static wheel load as "<value> N (<share> %)" and rate the share against
    /// the bike-specific flat-floor expectation computed from the spring setup at the
    /// centre of the sag target. The Target column shows that expected flat-share so the
    /// user can see at a glance how far the trail-mean diverges from the static reference.
    /// Falls back to a generic 35/45 % band when no flat reference is available.
    /// </summary>
    private static void SetForceWithShare(BalanceMetricRow row, double? value,
        double? f, double? r, double? fFlat, double? rFlat, bool isFront)
    {
        if (!value.HasValue) { row.Value = "—"; row.Status = BalanceStatus.Unknown; row.Target = ""; return; }
        if (!(f.HasValue && r.HasValue && f.Value + r.Value > 0))
        {
            row.Value = string.Format(CultureInfo.InvariantCulture, "{0:0} N", value.Value);
            row.Status = BalanceStatus.Unknown;
            row.Target = "";
            return;
        }

        var share = 100.0 * value.Value / (f.Value + r.Value);
        row.Value = string.Format(CultureInfo.InvariantCulture, "{0:0} N ({1:0.0} %)", value.Value, share);

        if (fFlat.HasValue && rFlat.HasValue && fFlat.Value + rFlat.Value > 0)
        {
            // Bike-specific expectation. Compare share against the flat-floor reference share
            // for this axle. Tolerance is ±3 pp (good) / ±6 pp (acceptable) — tighter than the
            // generic band because we're comparing to the rider's own setup, not a fleet average.
            var flatShareFront = 100.0 * fFlat.Value / (fFlat.Value + rFlat.Value);
            var flatShareThis  = isFront ? flatShareFront : 100.0 - flatShareFront;
            row.Target = string.Format(CultureInfo.InvariantCulture, "≈ {0:0.0} % flat", flatShareThis);
            var dev = Math.Abs(share - flatShareThis);
            row.Status =
                dev <= 3.0 ? BalanceStatus.Good
              : dev <= 6.0 ? BalanceStatus.Acceptable
              :              BalanceStatus.Critical;
        }
        else
        {
            // Fallback: generic 35–45 % front-share band when the flat reference is missing
            // (e.g. cached metrics from before the v2 schema).
            var frontShare = isFront ? share : 100.0 - share;
            row.Target = isFront ? "≈ 35–45 % share" : "≈ 55–65 % share";
            row.Status =
                (frontShare >= 35 && frontShare <= 45) ? BalanceStatus.Good
              : (frontShare >= 30 && frontShare <= 50) ? BalanceStatus.Acceptable
              :                                          BalanceStatus.Critical;
        }
    }

    /// <summary>
    /// Same shape as <see cref="SetSagBand"/> but with caller-supplied acceptable bounds
    /// and a configurable format string. Used for metrics whose acceptable buffer can't be
    /// expressed as a fixed ±2 around the good range (e.g. dynamic range, where good is
    /// 200–700 % and acceptable still extends to 100 / 800 %).
    /// </summary>
    private static void SetRangeBand(BalanceMetricRow row, double? value, string fmt,
        double goodLo, double goodHi, double accLo, double accHi)
    {
        if (!value.HasValue) { row.Value = "—"; row.Status = BalanceStatus.Unknown; return; }
        row.Value = string.Format(CultureInfo.InvariantCulture, fmt, value.Value);
        var v = value.Value;
        row.Status =
            (v >= goodLo && v <= goodHi) ? BalanceStatus.Good
          : (v >= accLo  && v <= accHi)  ? BalanceStatus.Acceptable
          :                                BalanceStatus.Critical;
    }

    private static void SetEnergyRatioDb(BalanceMetricRow row, double? value)
    {
        if (!value.HasValue) { row.Value = "—"; row.Status = BalanceStatus.Unknown; return; }
        row.Value = string.Format(CultureInfo.InvariantCulture, "{0:+0.0;-0.0;0.0} dB", value.Value);
        var abs = Math.Abs(value.Value);
        row.Status = abs <= 2.0 ? BalanceStatus.Good
            : abs <= 4.0        ? BalanceStatus.Acceptable
            :                     BalanceStatus.Critical;
    }

    private static void SetCoherence(BalanceMetricRow row, double? value, bool higherIsBetter, double? goodCutoff = null)
    {
        if (!value.HasValue) { row.Value = "—"; row.Status = BalanceStatus.Unknown; return; }
        row.Value = string.Format(CultureInfo.InvariantCulture, "{0:0.00}", value.Value);
        // Acceptable extends ±0.2 from the "good" cutoff. Tiny epsilon keeps a value that
        // matches the cutoff at two-decimal display precision (e.g. 0.10) on the Good side
        // even when the underlying double is a hair above (0.10000000000000001…).
        const double buffer = 0.2;
        const double epsilon = 5e-3;
        if (higherIsBetter)
        {
            double cutoff = goodCutoff ?? 0.7;
            row.Status = value.Value >= cutoff - epsilon          ? BalanceStatus.Good
                : value.Value >= cutoff - buffer - epsilon        ? BalanceStatus.Acceptable
                :                                                   BalanceStatus.Critical;
        }
        else
        {
            double cutoff = goodCutoff ?? 0.4;
            row.Status = value.Value <= cutoff + epsilon          ? BalanceStatus.Good
                : value.Value <= cutoff + buffer + epsilon        ? BalanceStatus.Acceptable
                :                                                   BalanceStatus.Critical;
        }
    }

    private static void SetSagBand(BalanceMetricRow row, double? value, double goodLo, double goodHi)
    {
        if (!value.HasValue) { row.Value = "—"; row.Status = BalanceStatus.Unknown; return; }
        row.Value = string.Format(CultureInfo.InvariantCulture, "{0:0.0} %", value.Value);
        var v = value.Value;
        row.Status = (v >= goodLo && v <= goodHi) ? BalanceStatus.Good
            : (v >= goodLo - 2 && v <= goodHi + 2) ? BalanceStatus.Acceptable
            : BalanceStatus.Critical;
    }

    private static void SetThreshold(BalanceMetricRow row, double? value, string fmt,
        double goodCutoff, double acceptableCutoff, bool lowerIsBetter)
    {
        if (!value.HasValue) { row.Value = "—"; row.Status = BalanceStatus.Unknown; return; }
        row.Value = string.Format(CultureInfo.InvariantCulture, fmt, value.Value);
        var v = value.Value;
        if (lowerIsBetter)
        {
            row.Status = v <= goodCutoff ? BalanceStatus.Good
                : v <= acceptableCutoff ? BalanceStatus.Acceptable
                : BalanceStatus.Critical;
        }
        else
        {
            row.Status = v > goodCutoff ? BalanceStatus.Good
                : v > acceptableCutoff ? BalanceStatus.Acceptable
                : BalanceStatus.Critical;
        }
    }

    private static void SetSimple(BalanceMetricRow row, double? value, string fmt)
    {
        if (!value.HasValue) { row.Value = "—"; row.Status = BalanceStatus.Unknown; return; }
        row.Value = string.Format(CultureInfo.InvariantCulture, fmt, value.Value);
        row.Status = BalanceStatus.Unknown;
    }

    private static void SetCount(BalanceMetricRow row, int? value)
    {
        if (!value.HasValue) { row.Value = "—"; row.Status = BalanceStatus.Unknown; return; }
        row.Value = $"{value.Value} times";
        row.Status = value.Value == 0 ? BalanceStatus.Good
            : value.Value <= 5        ? BalanceStatus.Acceptable
            :                           BalanceStatus.Critical;
    }

    // Michelson-style imbalance index centered on 0; positive = front, negative = rear.
    // Always shows an explicit sign so the bias direction is visible at a glance.
    private static void SetSignedBand(BalanceMetricRow row, double? value,
        double goodLo, double goodHi, double accLo, double accHi)
    {
        if (!value.HasValue) { row.Value = "—"; row.Status = BalanceStatus.Unknown; return; }
        row.Value = string.Format(CultureInfo.InvariantCulture, "{0:+0.00;-0.00;0.00}", value.Value);
        var v = value.Value;
        row.Status = (v >= goodLo && v <= goodHi) ? BalanceStatus.Good
            : (v >= accLo && v <= accHi) ? BalanceStatus.Acceptable
            : BalanceStatus.Critical;
    }

    private static void SetMsd(BalanceMetricRow row, double? value)
    {
        if (!value.HasValue) { row.Value = "—"; row.Status = BalanceStatus.Unknown; return; }
        row.Value = string.Format(CultureInfo.InvariantCulture, "{0:+0.00;-0.00;0.00} %", value.Value);
        var abs = Math.Abs(value.Value);
        row.Status = abs <= 5  ? BalanceStatus.Good
            : abs <= 15        ? BalanceStatus.Acceptable
            :                    BalanceStatus.Critical;
    }

    // Rebound MSD: asymmetric — rear should NOT rebound faster than front
    // (positive MSD = kick on rebound). Negative values up to −10% are still
    // good (rear slightly slower is acceptable / preferred). Critical at
    // |value| ≥ 15%.
    private static void SetMsdRebound(BalanceMetricRow row, double? value)
    {
        if (!value.HasValue) { row.Value = "—"; row.Status = BalanceStatus.Unknown; return; }
        row.Value = string.Format(CultureInfo.InvariantCulture, "{0:+0.00;-0.00;0.00} %", value.Value);
        var v = value.Value;
        row.Status = (v >= -10 && v <= 0) ? BalanceStatus.Good
            : Math.Abs(v) >= 15            ? BalanceStatus.Critical
            :                                BalanceStatus.Acceptable;
    }

    /// <summary>
    /// Discipline-specific body eigenfrequency target bands. Derived from
    /// f_n = (1/2π)·√(g/x_static) at sag 20–35 % for typical travel per category:
    ///   XC        ~80 mm  → 3.0–3.9 Hz
    ///   Enduro    ~160 mm → 2.1–3.2 Hz
    ///   Downhill  ~200 mm → 1.7–2.5 Hz
    /// Front lower bound equals Rear lower bound (Vorsprung); Front upper bound
    /// runs ~0.3 Hz higher to allow for a slightly stiffer front. See FrontFreq comment.
    /// </summary>
    private static (double frontLo, double frontHi, double rearLo, double rearHi) GetFreqBands(Discipline d) => d switch
    {
        Discipline.XC       => (3.0, 3.9, 3.0, 3.6),
        Discipline.Downhill => (1.7, 2.5, 1.7, 2.3),
        _                   => (2.1, 3.2, 2.1, 2.9), // Enduro / default
    };

    private static void SetFreqBand(BalanceMetricRow row, double? value, double goodLo, double goodHi)
    {
        if (!value.HasValue) { row.Value = "—"; row.Status = BalanceStatus.Unknown; return; }
        row.Value = string.Format(CultureInfo.InvariantCulture, "{0:0.00} Hz", value.Value);
        var v = value.Value;
        // ±0.5 Hz acceptable margin around the target band.
        row.Status = (v >= goodLo && v <= goodHi) ? BalanceStatus.Good
            : (v >= goodLo - 0.5 && v <= goodHi + 0.5) ? BalanceStatus.Acceptable
            : BalanceStatus.Critical;
    }

    private static void SetFreqDiff(BalanceMetricRow row, double? value)
    {
        if (!value.HasValue) { row.Value = "—"; row.Status = BalanceStatus.Unknown; return; }
        row.Value = string.Format(CultureInfo.InvariantCulture, "{0:0.00} Hz", value.Value);
        var v = value.Value;
        row.Status = v <= 0.4 ? BalanceStatus.Good
            : v <= 0.7        ? BalanceStatus.Acceptable
            :                   BalanceStatus.Critical;
    }
}

public partial class BalancePageViewModel() : PageViewModelBase("Balance")
{
    [ObservableProperty] private SvgImage? combinedBalance;
    [ObservableProperty] private SvgImage? compressionBalance;
    [ObservableProperty] private SvgImage? reboundBalance;
    [ObservableProperty] private SvgImage? combinedTravelFft;
    [ObservableProperty] private SvgImage? combinedTravelFftHigh;
    [ObservableProperty] private SvgImage? combinedVelocityFft;

    public BalanceMetricsViewModel Metrics { get; } = new();
}
