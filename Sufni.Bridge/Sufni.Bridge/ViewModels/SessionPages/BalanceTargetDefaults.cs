using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Sufni.Bridge.Models;

namespace Sufni.Bridge.ViewModels.SessionPages;

/// <summary>
/// Shape of a balance-metric's target band. Determines how the yellow (acceptable) band is
/// derived from the user-editable green range, and how a measured value is classified.
/// </summary>
public enum MetricShape
{
    /// <summary>green = [lo, hi]; yellow = [lo − offLo, hi + offHi] inclusive at the outer edge.</summary>
    Band,
    /// <summary>green = v ≤ cutoff; yellow = v ≤ cutoff + offLo. Lower is better.</summary>
    CutoffLower,
    /// <summary>green = v &gt; cutoff; yellow = v &gt; cutoff − offLo. Higher is better.</summary>
    CutoffUpper,
    /// <summary>green = [lo, hi]; yellow = (lo − offLo, hi + offHi) exclusive at the outer edge.</summary>
    SignedBand,
}

/// <summary>
/// Definition of one editable balance metric: its persistence key, shape, default green
/// bounds, the per-side green→yellow offsets, the measured-value format, and a formatter that
/// renders the target ("Soll-Wert") string from the (possibly edited) green bounds.
/// </summary>
public sealed record BalanceMetricDef(
    string Key,
    MetricShape Shape,
    double DefaultGreenMin,        // for Cutoff* shapes this IS the cutoff; DefaultGreenMax is null
    double? DefaultGreenMax,
    double YellowLowOffset,
    double YellowHighOffset,
    string ValueFormat,
    Func<double, double?, string> TargetFormatter)
{
    /// <summary>True for shapes with both a min and a max bound (two editor fields).</summary>
    public bool HasRange => DefaultGreenMax.HasValue;
}

/// <summary>
/// Single source of truth for the user-editable balance-metric targets. Default green bounds
/// and yellow offsets are captured verbatim from the original hardcoded thresholds in
/// <see cref="BalanceMetricsViewModel"/>, so with no user override the displayed targets and
/// status colors are unchanged.
///
/// To make another metric editable: add an entry here, set Key/IsEditable on its row in
/// BalanceMetricsViewModel, and wrap its target cell in the editor Panel in BalancePageView.axaml.
/// </summary>
public static class BalanceTargetDefaults
{
    // Invariant integer formatting, matching the original target literals (e.g. "23–28 %").
    private static string I(double v) => v.ToString("0", CultureInfo.InvariantCulture);

    public static readonly IReadOnlyDictionary<string, BalanceMetricDef> All =
        new[]
        {
            new BalanceMetricDef("FrontSag", MetricShape.Band, 23, 28, 2, 2, "{0:0.0} %",
                (lo, hi) => $"{I(lo)}–{I(hi!.Value)} %"),
            new BalanceMetricDef("RearSag", MetricShape.Band, 28, 33, 2, 2, "{0:0.0} %",
                (lo, hi) => $"{I(lo)}–{I(hi!.Value)} %"),
            new BalanceMetricDef("SagDiff", MetricShape.CutoffLower, 5, null, 3, 0, "{0:0.0} pp",
                (cut, _) => $"≤ {I(cut)} pp"),
            new BalanceMetricDef("FrontP95", MetricShape.CutoffUpper, 55, null, 5, 0, "{0:0.0} %",
                (cut, _) => $"> {I(cut)} %"),
            new BalanceMetricDef("RearP95", MetricShape.CutoffUpper, 55, null, 5, 0, "{0:0.0} %",
                (cut, _) => $"> {I(cut)} %"),
            new BalanceMetricDef("RebMsd", MetricShape.SignedBand, -10, 0, 5, 15, "{0:+0.00;-0.00;0.00} %",
                (lo, hi) => $"{I(lo)} to {I(hi!.Value)} %"),
            new BalanceMetricDef("PitchStability", MetricShape.CutoffLower, 1.0, null, 1.0, 0, "{0:0.00}°",
                (cut, _) => $"≤ {cut.ToString("0.0", CultureInfo.InvariantCulture)}°"),
            new BalanceMetricDef("GoutSymmetry", MetricShape.CutoffLower, 10, null, 10, 0, "{0:0} %",
                (cut, _) => $"≤ {I(cut)} %"),
        }.ToDictionary(d => d.Key);

    /// <summary>
    /// Default green bounds for a metric. Discipline is accepted so per-discipline defaults can
    /// be introduced later; today the six editable metrics share one default per metric.
    /// </summary>
    public static (double min, double? max) DefaultGreen(string key, Discipline? discipline = null)
    {
        var def = All[key];
        return (def.DefaultGreenMin, def.DefaultGreenMax);
    }

    /// <summary>
    /// Classify a measured value against the green bounds, deriving the yellow band per the
    /// metric's shape. Reproduces the original threshold logic exactly when called with the
    /// default green bounds (note Band uses an inclusive outer edge, SignedBand exclusive).
    /// </summary>
    public static BalanceStatus Classify(BalanceMetricDef def, double greenMin, double? greenMax, double value)
    {
        switch (def.Shape)
        {
            case MetricShape.Band:
            {
                var hi = greenMax!.Value;
                if (value >= greenMin && value <= hi) return BalanceStatus.Good;
                if (value >= greenMin - def.YellowLowOffset && value <= hi + def.YellowHighOffset)
                    return BalanceStatus.Acceptable;
                return BalanceStatus.Critical;
            }
            case MetricShape.SignedBand:
            {
                var hi = greenMax!.Value;
                if (value >= greenMin && value <= hi) return BalanceStatus.Good;
                // Exclusive outer edge: a value exactly at the offset bound is Critical
                // (reproduces SetMsdRebound's |v| ≥ 15 ⇒ Critical).
                if (value > greenMin - def.YellowLowOffset && value < hi + def.YellowHighOffset)
                    return BalanceStatus.Acceptable;
                return BalanceStatus.Critical;
            }
            case MetricShape.CutoffLower:
            {
                if (value <= greenMin) return BalanceStatus.Good;
                if (value <= greenMin + def.YellowLowOffset) return BalanceStatus.Acceptable;
                return BalanceStatus.Critical;
            }
            case MetricShape.CutoffUpper:
            {
                if (value > greenMin) return BalanceStatus.Good;
                if (value > greenMin - def.YellowLowOffset) return BalanceStatus.Acceptable;
                return BalanceStatus.Critical;
            }
            default:
                return BalanceStatus.Unknown;
        }
    }

    /// <summary>Effective green bounds: a stored override (when its min is set), else the registry default.</summary>
    public static (double min, double? max) EffectiveGreen(
        IReadOnlyDictionary<string, (double? min, double? max)>? overrides, string key, Discipline? discipline = null)
    {
        if (overrides is not null && overrides.TryGetValue(key, out var o) && o.min.HasValue)
            return (o.min.Value, o.max);
        return DefaultGreen(key, discipline);
    }

    /// <summary>
    /// Expected chassis-pitch band (degrees, nose-down positive) implied by the front/rear SAG
    /// green ranges and bike geometry. Used as the μ metric's traffic-light reference and the
    /// pitch plot's expected band. Returns null if geometry or the SAG ranges are unavailable.
    /// </summary>
    public static (double minDeg, double maxDeg)? ExpectedPitchBand(
        (double min, double? max) frontSagBand, (double min, double? max) rearSagBand,
        double? maxFrontTravelMm, double? maxRearTravelMm, double? wheelbaseMm)
    {
        if (maxFrontTravelMm is not > 0 || maxRearTravelMm is not > 0 || wheelbaseMm is not > 0) return null;
        if (frontSagBand.max is null || rearSagBand.max is null) return null;
        double Pitch(double frontPct, double rearPct)
        {
            var frontMm = frontPct / 100.0 * maxFrontTravelMm.Value;
            var rearMm = rearPct / 100.0 * maxRearTravelMm.Value;
            return -Math.Atan2(rearMm - frontMm, wheelbaseMm.Value) * 180.0 / Math.PI;
        }
        var maxDeg = Pitch(frontSagBand.max.Value, rearSagBand.min);
        var minDeg = Pitch(frontSagBand.min, rearSagBand.max.Value);
        return (minDeg, maxDeg);
    }
}
