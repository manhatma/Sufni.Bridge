using System;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Svg.Skia;
using CommunityToolkit.Mvvm.ComponentModel;
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
    public BalanceMetricRow FrontFreq     { get; } = new() { Label = "Front Eigenfreq.", Target = "1.8–3.5 Hz" };
    public BalanceMetricRow RearFreq      { get; } = new() { Label = "Rear Eigenfreq.",  Target = "1.8–3.5 Hz" };
    public BalanceMetricRow FreqDiff      { get; } = new() { Label = "Frequency-Diff",   Target = "≤ 0.4 Hz" };
    public BalanceMetricRow PeakAmpRatio  { get; } = new() { Label = "Peak Amp F/R",     Target = "0.9–1.1" };

    public void Apply(BalanceMetrics m)
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
        SetSimple(FrontFreq, m.FrontPeakFrequencyHz, "{0:0.00} Hz");
        SetSimple(RearFreq,  m.RearPeakFrequencyHz,  "{0:0.00} Hz");
        SetFreqDiff(FreqDiff, m.FrequencyDifferenceHz);
        SetRatioBand(PeakAmpRatio, m.PeakAmplitudeRatio, 0.9, 1.1, 0.8, 1.2);
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

    public BalanceMetricsViewModel Metrics { get; } = new();
}
