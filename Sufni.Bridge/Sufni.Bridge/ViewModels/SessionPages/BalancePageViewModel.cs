using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Svg.Skia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Sufni.Bridge.Models;
using Sufni.Bridge.Models.Telemetry;
using Sufni.Bridge.Services;

namespace Sufni.Bridge.ViewModels.SessionPages;

public enum BalanceStatus { Unknown, Good, Acceptable, Critical }

public partial class BalanceMetricRow : ObservableObject
{
    [ObservableProperty] private string label = "";
    [ObservableProperty] private string value = "—";
    [ObservableProperty] private string target = "";
    [ObservableProperty] private BalanceStatus status = BalanceStatus.Unknown;

    // Identity & edit affordance — set once at construction for the editable rows.
    public string Key { get; init; } = "";
    public bool IsEditable { get; init; }
    public bool HasRange { get; init; }

    // Edit state, driven by the parent BalanceMetricsViewModel.IsEditing toggle.
    [ObservableProperty] private bool isEditing;
    [ObservableProperty] private double? greenMin;
    [ObservableProperty] private double? greenMax;

    public bool ShowEditors => IsEditing && IsEditable;
    public bool ShowTargetText => !ShowEditors;
    public bool ShowMaxField => HasRange;

    public IBrush ValueBrush => Status switch
    {
        BalanceStatus.Good       => new SolidColorBrush(Color.FromRgb(0x6C, 0xC4, 0x4A)),
        BalanceStatus.Acceptable => new SolidColorBrush(Color.FromRgb(0xE0, 0xB8, 0x3A)),
        BalanceStatus.Critical   => new SolidColorBrush(Color.FromRgb(0xE0, 0x6A, 0x55)),
        _                        => new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0)),
    };

    partial void OnStatusChanged(BalanceStatus value) => OnPropertyChanged(nameof(ValueBrush));

    partial void OnIsEditingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowEditors));
        OnPropertyChanged(nameof(ShowTargetText));
    }
}

public partial class BalanceMetricsViewModel : ObservableObject
{
    public BalanceMetricRow FrontSag      { get; } = new() { Label = "Front SAG (dyn.)", Target = "23–28 %", Key = "FrontSag", IsEditable = true, HasRange = true };
    public BalanceMetricRow RearSag       { get; } = new() { Label = "Rear SAG (dyn.)",  Target = "28–33 %", Key = "RearSag",  IsEditable = true, HasRange = true };
    public BalanceMetricRow SagDiff       { get; } = new() { Label = "Sag-Diff |F−R|",   Target = "≤ 5 pp",  Key = "SagDiff",  IsEditable = true };
    public BalanceMetricRow FrontP95      { get; } = new() { Label = "Front 95th",       Target = "> 55 %",  Key = "FrontP95", IsEditable = true };
    public BalanceMetricRow RearP95       { get; } = new() { Label = "Rear 95th",        Target = "> 55 %",  Key = "RearP95",  IsEditable = true };
    public BalanceMetricRow P95Diff       { get; } = new() { Label = "95th-Diff |F−R|",  Target = "≤ 5 pp" };
    public BalanceMetricRow EffectiveHeadAngle { get; } = new() { Label = "Eff. Head Angle", Target = "" };
    public BalanceMetricRow FrontBO       { get; } = new() { Label = "Front Bottom-out", Target = "≈ 0" };
    public BalanceMetricRow RearBO        { get; } = new() { Label = "Rear Bottom-out",  Target = "≈ 0" };
    public BalanceMetricRow CompVelRatio  { get; } = new() { Label = "Comp Vel F/R",     Target = "−0.08 … +0.07" };
    public BalanceMetricRow RebVelRatio   { get; } = new() { Label = "Reb Vel F/R",      Target = "0.00 … +0.07" };
    public BalanceMetricRow CompMsd       { get; } = new() { Label = "MSD Compression",  Target = "≈ 0" };
    public BalanceMetricRow RebMsd        { get; } = new() { Label = "MSD Rebound",      Target = "−10 to 0 %", Key = "RebMsd", IsEditable = true, HasRange = true };
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

    // --- user-editable targets (per discipline) ------------------------------------------
    // Cached inputs so the editor can re-color in place without recomputing telemetry.
    private BalanceMetrics? lastMetrics;
    private Discipline? lastDiscipline;
    private readonly Dictionary<string, (double? min, double? max)> targetOverrides = new();

    [ObservableProperty] private bool isEditing;

    private BalanceMetricRow[] EditableRows => [FrontSag, RearSag, SagDiff, FrontP95, RearP95, RebMsd];

    partial void OnIsEditingChanged(bool value)
    {
        foreach (var row in EditableRows)
        {
            if (value) SeedEditor(row);
            row.IsEditing = value;
        }
    }

    // Effective green bounds: a stored override, else the registry default.
    private (double min, double? max) EffectiveGreen(string key, Discipline? discipline)
    {
        if (targetOverrides.TryGetValue(key, out var o) && o.min.HasValue)
            return (o.min.Value, o.max);
        return BalanceTargetDefaults.DefaultGreen(key, discipline);
    }

    private void SeedEditor(BalanceMetricRow row)
    {
        var (min, max) = EffectiveGreen(row.Key, lastDiscipline);
        row.GreenMin = min;
        row.GreenMax = max;
    }

    // Sets Value/Status for an editable metric from its effective green bounds, deriving the
    // yellow band via the registry, and regenerates the Target string so it tracks edits.
    private void ApplyEditable(BalanceMetricRow row, double? value, Discipline? discipline)
    {
        var def = BalanceTargetDefaults.All[row.Key];
        var (min, max) = EffectiveGreen(row.Key, discipline);
        row.Target = def.TargetFormatter(min, max);
        if (!value.HasValue) { row.Value = "—"; row.Status = BalanceStatus.Unknown; return; }
        row.Value = string.Format(CultureInfo.InvariantCulture, def.ValueFormat, value.Value);
        row.Status = BalanceTargetDefaults.Classify(def, min, max, value.Value);
    }

    [RelayCommand]
    private async Task ConfirmEdit()
    {
        var db = App.Current?.Services?.GetService<IDatabaseService>();
        var discipline = lastDiscipline ?? Discipline.Enduro;
        foreach (var row in EditableRows)
        {
            var def = BalanceTargetDefaults.All[row.Key];
            double? min = row.GreenMin;
            double? max = def.HasRange ? row.GreenMax : null;
            if (!IsValidGreen(def, min, max)) { SeedEditor(row); continue; }

            var (defMin, defMax) = BalanceTargetDefaults.DefaultGreen(row.Key, discipline);
            var isDefault = NearlyEqual(min!.Value, defMin) && NullableNearlyEqual(max, defMax);
            if (isDefault)
            {
                targetOverrides.Remove(row.Key);
                if (db is not null) await db.DeleteBalanceTargetOverrideAsync(discipline, row.Key);
            }
            else
            {
                targetOverrides[row.Key] = (min, max);
                if (db is not null)
                    await db.PutBalanceTargetOverrideAsync(new BalanceTargetOverride
                    {
                        Discipline = discipline, MetricKey = row.Key, GreenMin = min, GreenMax = max
                    });
            }
        }

        IsEditing = false;
        if (lastMetrics is not null) Apply(lastMetrics, lastDiscipline);
    }

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    [RelayCommand]
    private void ResetMetric(BalanceMetricRow? row)
    {
        if (row is null) return;
        var (min, max) = BalanceTargetDefaults.DefaultGreen(row.Key, lastDiscipline);
        row.GreenMin = min;
        row.GreenMax = max;
    }

    private static bool IsValidGreen(BalanceMetricDef def, double? min, double? max)
    {
        if (min is null || double.IsNaN(min.Value) || double.IsInfinity(min.Value)) return false;
        if (!def.HasRange) return true;
        if (max is null || double.IsNaN(max.Value) || double.IsInfinity(max.Value)) return false;
        return min.Value < max.Value;
    }

    private static bool NearlyEqual(double a, double b) => Math.Abs(a - b) < 1e-9;
    private static bool NullableNearlyEqual(double? a, double? b)
        => (a is null && b is null) || (a.HasValue && b.HasValue && NearlyEqual(a.Value, b.Value));

    public void Apply(BalanceMetrics m, Discipline? discipline = null,
                      IReadOnlyDictionary<string, (double? min, double? max)>? overrideMap = null)
    {
        lastMetrics = m;
        lastDiscipline = discipline;
        if (overrideMap is not null)
        {
            targetOverrides.Clear();
            foreach (var kv in overrideMap) targetOverrides[kv.Key] = kv.Value;
        }

        ApplyEditable(FrontSag, m.FrontSagPct,     discipline);
        ApplyEditable(RearSag,  m.RearSagPct,      discipline);
        ApplyEditable(SagDiff,  m.SagDifferencePp, discipline);
        ApplyEditable(FrontP95, m.FrontP95Pct,     discipline);
        ApplyEditable(RearP95,  m.RearP95Pct,      discipline);
        var p95Diff = (m.FrontP95Pct.HasValue && m.RearP95Pct.HasValue)
            ? (double?)Math.Abs(m.FrontP95Pct.Value - m.RearP95Pct.Value)
            : null;
        SetThreshold(P95Diff, p95Diff, "{0:0.0} pp", 5.0, 10.0, lowerIsBetter: true);
        SetHeadAngle(EffectiveHeadAngle, m.HeadAngleStaticDeg, m.HeadAngleShiftDeg);
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
        ApplyEditable(RebMsd, m.ReboundMsd, discipline);
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

    private static void SetHeadAngle(BalanceMetricRow row, double? staticDeg, double? shiftDeg)
    {
        if (!staticDeg.HasValue || !shiftDeg.HasValue)
        {
            row.Value = "—";
            row.Target = "";
            row.Status = BalanceStatus.Unknown;
            return;
        }
        row.Value = string.Format(CultureInfo.InvariantCulture, "{0:0.0}°", staticDeg.Value + shiftDeg.Value);
        row.Target = string.Format(CultureInfo.InvariantCulture, "{0:0.0}°", staticDeg.Value);
        row.Status = BalanceStatus.Unknown;
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
        Discipline.Trail    => (2.5, 3.5, 2.5, 3.2), // new
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
