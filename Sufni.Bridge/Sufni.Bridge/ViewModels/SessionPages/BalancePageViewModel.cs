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
    public BalanceMetricRow FrontSag      { get; } = new() { Label = "Front Sag",        Target = "23–28 %" };
    public BalanceMetricRow RearSag       { get; } = new() { Label = "Rear Sag",         Target = "28–33 %" };
    public BalanceMetricRow SagDiff       { get; } = new() { Label = "Sag-Diff |F−R|",   Target = "≤ 5 pp" };
    public BalanceMetricRow FrontP95      { get; } = new() { Label = "Front 95th",       Target = "—" };
    public BalanceMetricRow RearP95       { get; } = new() { Label = "Rear 95th",        Target = "—" };
    public BalanceMetricRow P95Diff       { get; } = new() { Label = "95th-Diff |F−R|",  Target = "≤ 5 pp" };
    public BalanceMetricRow FrontBO       { get; } = new() { Label = "Front Bottom-out", Target = "low" };
    public BalanceMetricRow RearBO        { get; } = new() { Label = "Rear Bottom-out",  Target = "low" };
    public BalanceMetricRow CompVelRatio  { get; } = new() { Label = "Comp Vel F/R",     Target = "0.85–1.15" };
    public BalanceMetricRow RebVelRatio   { get; } = new() { Label = "Reb Vel F/R",      Target = "1.00–1.15" };
    public BalanceMetricRow CompMsd       { get; } = new() { Label = "MSD Compression",  Target = "≈ 0" };
    public BalanceMetricRow RebMsd        { get; } = new() { Label = "MSD Rebound",      Target = "≈ 0" };
    // Discipline-dependent eigenfrequency targets — see GetFreqBand below.
    // Front sits ~0.2–0.4 Hz higher than Rear so the rear absorbs the bump first
    // and the bike doesn't pitch forward under compression (Ortega 2008; NSMB;
    // Vorsprung tuning notes). Defaults are Enduro; updated when Apply runs.
    public BalanceMetricRow FrontFreq     { get; } = new() { Label = "Front Eigenfreq.", Target = "2.2–3.5 Hz" };
    public BalanceMetricRow RearFreq      { get; } = new() { Label = "Rear Eigenfreq.",  Target = "1.8–3.0 Hz" };
    public BalanceMetricRow FreqDiff      { get; } = new() { Label = "Frequency-Diff",   Target = "≤ 0.4 Hz" };
    public BalanceMetricRow PeakAmpRatio  { get; } = new() { Label = "Peak Amp F/R",     Target = "0.9–1.1" };

    // Front/Rear travel-energy distribution per band (10·log10(F/R)).
    // Low/Mid split is discipline-aware (see TelemetryData.FrequencySplitFor); High band fix 8–40 Hz.
    public BalanceMetricRow LowEnergyRatio  { get; } = new() { Label = "Energy F/R Low",  Target = "0 dB ±2" };
    public BalanceMetricRow MidEnergyRatio  { get; } = new() { Label = "Energy F/R Mid",  Target = "0 dB ±2" };
    public BalanceMetricRow HighEnergyRatio { get; } = new() { Label = "Energy F/R (8–40 Hz)", Target = "0 dB ±2" };

    // Front/Rear coupling per band (mean magnitude-squared coherence γ²).
    public BalanceMetricRow LowCoherence  { get; } = new() { Label = "Coherence Low",  Target = "≥ 0.7" };
    public BalanceMetricRow MidCoherence  { get; } = new() { Label = "Coherence Mid",  Target = "≥ 0.7" };
    public BalanceMetricRow HighCoherence { get; } = new() { Label = "Coherence (8–40 Hz)", Target = "≥ 0.7" };

    public void Apply(BalanceMetrics m, Discipline? discipline = null)
    {
        SetSagBand(FrontSag, m.FrontSagPct, 23, 28);
        SetSagBand(RearSag,  m.RearSagPct,  28, 33);
        SetThreshold(SagDiff, m.SagDifferencePp, "{0:0.0} pp", 5.0, 8.0, lowerIsBetter: true);
        SetSimple(FrontP95, m.FrontP95Pct, "{0:0.0} %");
        SetSimple(RearP95,  m.RearP95Pct,  "{0:0.0} %");
        var p95Diff = (m.FrontP95Pct.HasValue && m.RearP95Pct.HasValue)
            ? (double?)Math.Abs(m.FrontP95Pct.Value - m.RearP95Pct.Value)
            : null;
        SetThreshold(P95Diff, p95Diff, "{0:0.0} pp", 5.0, 10.0, lowerIsBetter: true);
        SetCount(FrontBO, m.FrontBottomouts);
        SetCount(RearBO,  m.RearBottomouts);
        SetRatioBand(CompVelRatio, m.CompressionVelocityRatio, 0.85, 1.15, 0.80, 1.20);
        // Rebound: rear should NOT rebound faster than front (kicks under jumps).
        // F/R < 1 means rear is faster → push the band upward.
        SetRatioBand(RebVelRatio,  m.ReboundVelocityRatio,     1.00, 1.15, 0.90, 1.20);
        SetMsd(CompMsd, m.CompressionMsd);
        SetMsd(RebMsd,  m.ReboundMsd);
        var (frontLo, frontHi, rearLo, rearHi) = GetFreqBands(discipline ?? Discipline.Enduro);
        FrontFreq.Target = string.Format(CultureInfo.InvariantCulture, "{0:0.0}–{1:0.0} Hz", frontLo, frontHi);
        RearFreq.Target  = string.Format(CultureInfo.InvariantCulture, "{0:0.0}–{1:0.0} Hz", rearLo,  rearHi);
        SetFreqBand(FrontFreq, m.FrontPeakFrequencyHz, frontLo, frontHi);
        SetFreqBand(RearFreq,  m.RearPeakFrequencyHz,  rearLo,  rearHi);
        SetFreqDiff(FreqDiff, m.FrequencyDifferenceHz);
        SetRatioBand(PeakAmpRatio, m.PeakAmplitudeRatio, 0.9, 1.1, 0.8, 1.2);

        var fSplit = m.FrequencySplitHz ?? 2.0;
        var fSplitStr = fSplit.ToString("0.0", CultureInfo.InvariantCulture);
        LowEnergyRatio.Label  = $"Energy F/R (0.2–{fSplitStr} Hz)";
        MidEnergyRatio.Label  = $"Energy F/R ({fSplitStr}–8 Hz)";
        HighEnergyRatio.Label = "Energy F/R (8–40 Hz)";
        SetEnergyRatioDb(LowEnergyRatio,  m.LowEnergyRatioDb);
        SetEnergyRatioDb(MidEnergyRatio,  m.MidEnergyRatioDb);
        SetEnergyRatioDb(HighEnergyRatio, m.HighEnergyRatioDb);

        LowCoherence.Label  = $"Coherence (0.2–{fSplitStr} Hz)";
        MidCoherence.Label  = $"Coherence ({fSplitStr}–8 Hz)";
        HighCoherence.Label = "Coherence (8–40 Hz)";
        SetCoherence(LowCoherence,  m.LowCoherence);
        SetCoherence(MidCoherence,  m.MidCoherence);
        SetCoherence(HighCoherence, m.HighCoherence);
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

    private static void SetCoherence(BalanceMetricRow row, double? value)
    {
        if (!value.HasValue) { row.Value = "—"; row.Status = BalanceStatus.Unknown; return; }
        row.Value = string.Format(CultureInfo.InvariantCulture, "{0:0.00}", value.Value);
        row.Status = value.Value >= 0.7 ? BalanceStatus.Good
            : value.Value >= 0.5        ? BalanceStatus.Acceptable
            :                             BalanceStatus.Critical;
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
        double goodMax, double acceptableMax, bool lowerIsBetter)
    {
        if (!value.HasValue) { row.Value = "—"; row.Status = BalanceStatus.Unknown; return; }
        row.Value = string.Format(CultureInfo.InvariantCulture, fmt, value.Value);
        var v = value.Value;
        if (lowerIsBetter)
        {
            row.Status = v <= goodMax ? BalanceStatus.Good
                : v <= acceptableMax ? BalanceStatus.Acceptable
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

    private static void SetRatioBand(BalanceMetricRow row, double? value,
        double goodLo, double goodHi, double accLo, double accHi)
    {
        if (!value.HasValue) { row.Value = "—"; row.Status = BalanceStatus.Unknown; return; }
        row.Value = string.Format(CultureInfo.InvariantCulture, "{0:0.00}", value.Value);
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
    /// Discipline-specific body eigenfrequency target bands. Driven by typical travel
    /// and spring-rate ranges per category:
    ///   XC        — short travel, stiff   → high f_n
    ///   Enduro    — medium travel         → mid f_n
    ///   Downhill  — long travel, soft     → low f_n
    /// In all categories Front sits 0.3 Hz higher than Rear (see comment on FrontFreq).
    /// </summary>
    private static (double frontLo, double frontHi, double rearLo, double rearHi) GetFreqBands(Discipline d) => d switch
    {
        Discipline.XC       => (2.8, 4.0, 2.5, 3.5),
        Discipline.Downhill => (1.5, 2.5, 1.3, 2.2),
        _                   => (2.2, 3.5, 1.8, 3.0), // Enduro / default
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

    public BalanceMetricsViewModel Metrics { get; } = new();
}
