using System;
using System.Collections.Generic;
using System.Globalization;

namespace Sufni.Bridge.Models.Telemetry;

public enum TuningSeverity { Info, Recommended, Critical }

public record TuningSuggestion(
    TuningSeverity Severity,
    string Component,   // "Fork" | "Shock" | "Balance"
    string Title,
    string Reason,
    int Score);

public sealed record TuningInputs(
    BalanceMetrics Metrics,
    VelocityBands? FrontBands,
    VelocityBands? RearBands,
    double? FrontPeakCompressionMmS,
    double? RearPeakCompressionMmS,
    double? FrontPeakReboundMmS,   // absolute value
    double? RearPeakReboundMmS,    // absolute value
    int? FrontCompressionStrokeCount,
    int? RearCompressionStrokeCount);

public static class TuningEngine
{
    // Sag bands (% of travel)
    private const double FrontSagGoodLo = 23, FrontSagGoodHi = 28;
    private const double RearSagGoodLo  = 28, RearSagGoodHi  = 33;
    private const double SagBandTolerance = 2;          // pp from band edge → still inside acceptable
    private const double SagCriticalDelta = 4;          // pp from band edge → Critical
    private const double SagDifferenceRecommended = 5;
    private const double SagDifferenceCritical = 8;

    // Travel
    private const double P95CriticalPct = 92;

    // Bottom-out share of compression strokes
    private const double BottomoutShareGoodMax = 0.02;
    private const double BottomoutShareCriticalMax = 0.05;

    // Velocity bands — perfect balance has each of LSC/HSC/LSR/HSR ≈ 25 % of total samples.
    // Same definition as the Damper-tab zones (% of all compression+rebound samples).
    private const double BandTargetPct = 25.0;
    private const double BandTolerancePp = 5.0;     // ± 5 pp from 25 % is fine
    private const double BandCriticalPp  = 10.0;    // > ± 10 pp → Critical

    // Peak velocities (mm/s)
    private const double PeakCompressionMax = 8000;
    private const double PeakCompressionCritical = 10000;
    private const double FrontPeakReboundLo = 1500, FrontPeakReboundHi = 2500;
    private const double RearPeakReboundLo  = 1000, RearPeakReboundHi  = 1800;
    private const double PeakReboundCriticalDelta = 500;  // mm/s outside good band → Critical

    // Rebound balance
    private const double ReboundRatioGoodLo = 0.90, ReboundRatioGoodHi = 1.20;

    public static IReadOnlyList<TuningSuggestion> Evaluate(TuningInputs i)
    {
        var m = i.Metrics;
        var list = new List<TuningSuggestion>();

        AddSagRule(list, "Fork",  m.FrontSagPct, FrontSagGoodLo, FrontSagGoodHi, "fork air pressure", "Front sag");
        AddSagRule(list, "Shock", m.RearSagPct,  RearSagGoodLo,  RearSagGoodHi,  "shock air pressure", "Rear sag");

        if (m.SagDifferencePp.HasValue && m.SagDifferencePp.Value > SagDifferenceRecommended)
        {
            var diff = m.SagDifferencePp.Value;
            var sev = diff > SagDifferenceCritical ? TuningSeverity.Critical : TuningSeverity.Recommended;
            list.Add(new TuningSuggestion(sev, "Balance",
                "Equalise front/rear sag",
                Fmt($"Sag difference is {diff:0.0} pp — target ≤ {SagDifferenceRecommended:0} pp"),
                Score(sev, 8)));
        }

        AddBottomoutRule(list, "Fork",  m.FrontBottomouts, i.FrontCompressionStrokeCount, m.FrontP95Pct, "fork");
        AddBottomoutRule(list, "Shock", m.RearBottomouts,  i.RearCompressionStrokeCount,  m.RearP95Pct,  "shock");

        AddBandRules(list, "Fork",  i.FrontBands, "fork");
        AddBandRules(list, "Shock", i.RearBands,  "shock");

        AddPeakCompressionRule(list, "Fork",  i.FrontPeakCompressionMmS, "fork");
        AddPeakCompressionRule(list, "Shock", i.RearPeakCompressionMmS,  "shock");

        AddPeakReboundRule(list, "Fork",  i.FrontPeakReboundMmS, FrontPeakReboundLo, FrontPeakReboundHi, "fork");
        AddPeakReboundRule(list, "Shock", i.RearPeakReboundMmS,  RearPeakReboundLo,  RearPeakReboundHi,  "shock");

        if (m.ReboundVelocityRatio.HasValue)
        {
            var r = m.ReboundVelocityRatio.Value;
            if (r < ReboundRatioGoodLo || r > ReboundRatioGoodHi)
            {
                list.Add(new TuningSuggestion(TuningSeverity.Recommended, "Balance",
                    r < ReboundRatioGoodLo
                        ? "Rear rebounds faster than front — slow rear or speed up front"
                        : "Front rebounds much faster than rear — slow front or speed up rear",
                    Fmt($"Reb-vel ratio F/R is {r:0.00} — target {ReboundRatioGoodLo:0.00}–{ReboundRatioGoodHi:0.00}"),
                    Score(TuningSeverity.Recommended, 4)));
            }
        }

        if (list.Count == 0)
        {
            list.Add(new TuningSuggestion(TuningSeverity.Info, "Balance",
                "Setup looks balanced",
                "No rules triggered — no changes recommended.",
                0));
        }

        list.Sort((a, b) =>
        {
            var s = ((int)b.Severity).CompareTo((int)a.Severity);
            return s != 0 ? s : b.Score.CompareTo(a.Score);
        });
        return list;
    }

    private static void AddSagRule(List<TuningSuggestion> list, string component,
        double? sagPct, double goodLo, double goodHi, string what, string label)
    {
        if (!sagPct.HasValue) return;
        var v = sagPct.Value;
        if (v >= goodLo && v <= goodHi) return;

        var distance = v < goodLo ? goodLo - v : v - goodHi;
        if (distance <= SagBandTolerance) return;            // close enough → no suggestion

        var sev = distance > SagCriticalDelta ? TuningSeverity.Critical : TuningSeverity.Recommended;
        var direction = v > goodHi ? $"Increase {what}" : $"Decrease {what}";
        list.Add(new TuningSuggestion(sev, component,
            direction,
            Fmt($"{label} is {v:0.0} % — target {goodLo:0}–{goodHi:0} %"),
            Score(sev, 9)));
    }

    private static void AddBottomoutRule(List<TuningSuggestion> list, string component,
        int? bottomouts, int? compressionStrokes, double? p95Pct, string what)
    {
        if (!bottomouts.HasValue) return;
        var hasBo = bottomouts.Value >= 1;
        var p95Critical = p95Pct.HasValue && p95Pct.Value > P95CriticalPct;
        if (!hasBo && !p95Critical) return;

        double? share = (compressionStrokes.HasValue && compressionStrokes.Value > 0)
            ? (double)bottomouts.Value / compressionStrokes.Value
            : null;

        var triggered = (share.HasValue && share.Value > BottomoutShareGoodMax) || p95Critical;
        if (!triggered) return;

        var sev = (share.HasValue && share.Value > BottomoutShareCriticalMax) || p95Critical
            ? TuningSeverity.Critical
            : TuningSeverity.Recommended;

        var reason = share.HasValue
            ? Fmt($"{bottomouts.Value} bottom-outs in {compressionStrokes} compressions ({share.Value * 100:0.0} %); P95 {p95Pct ?? 0:0.0} %")
            : Fmt($"{bottomouts.Value} bottom-outs; P95 {p95Pct ?? 0:0.0} %");

        list.Add(new TuningSuggestion(sev, component,
            $"Add {what} volume spacer or increase HSC",
            reason,
            Score(sev, 8)));
    }

    private static void AddBandRules(List<TuningSuggestion> list, string component,
        VelocityBands? bands, string what)
    {
        if (bands is null) return;
        // Skip when the bands clearly weren't computed (all near zero).
        if (bands.LowSpeedCompression + bands.HighSpeedCompression
          + bands.LowSpeedRebound     + bands.HighSpeedRebound < 50.0) return;

        // Unified rule (drives band time toward 25 %): closing damping in a high-speed band
        // truncates the velocity peak (less time above the threshold); opening damping in a
        // low-speed band lets the suspension transit the band faster (less time inside).
        // → HS-bands: above target → close. LS-bands: above target → open.
        AddBandRule(list, component, bands.HighSpeedCompression, "HSC", what,
                    openWhenAbove: false, weight: 6);
        AddBandRule(list, component, bands.LowSpeedCompression,  "LSC", what,
                    openWhenAbove: true,  weight: 5);
        AddBandRule(list, component, bands.HighSpeedRebound,     "HSR", what,
                    openWhenAbove: false, weight: 5);
        AddBandRule(list, component, bands.LowSpeedRebound,      "LSR", what,
                    openWhenAbove: true,  weight: 4);
    }

    private static void AddBandRule(List<TuningSuggestion> list, string component,
        double valuePct, string band, string what, bool openWhenAbove, int weight)
    {
        var deviation = valuePct - BandTargetPct;
        var absDev = Math.Abs(deviation);
        if (absDev <= BandTolerancePp) return;

        var sev = absDev > BandCriticalPp ? TuningSeverity.Critical : TuningSeverity.Recommended;
        var above = deviation > 0;
        // openWhenAbove encodes the click direction convention per band type.
        var open = above ? openWhenAbove : !openWhenAbove;
        var verb = open ? "Open" : "Close";
        list.Add(new TuningSuggestion(sev, component,
            $"{verb} {what} {band} by 1 click",
            Fmt($"{band} is {valuePct:0.0} % — target {BandTargetPct:0} % (±{BandTolerancePp:0} pp)"),
            Score(sev, weight)));
    }

    private static void AddPeakCompressionRule(List<TuningSuggestion> list, string component,
        double? peakMmS, string what)
    {
        if (!peakMmS.HasValue || peakMmS.Value <= PeakCompressionMax) return;
        var v = peakMmS.Value;
        var sev = v > PeakCompressionCritical ? TuningSeverity.Critical : TuningSeverity.Recommended;
        list.Add(new TuningSuggestion(sev, component,
            $"Increase {what} HSC or add volume spacer — very high compression speed",
            Fmt($"Peak compression speed {v:0} mm/s — target ≤ {PeakCompressionMax:0} mm/s"),
            Score(sev, 5)));
    }

    private static void AddPeakReboundRule(List<TuningSuggestion> list, string component,
        double? peakAbsMmS, double goodLo, double goodHi, string what)
    {
        if (!peakAbsMmS.HasValue) return;
        var v = peakAbsMmS.Value;
        if (v >= goodLo && v <= goodHi) return;

        var distance = v < goodLo ? goodLo - v : v - goodHi;
        var sev = distance > PeakReboundCriticalDelta ? TuningSeverity.Critical : TuningSeverity.Recommended;
        // Rebound clicks: distance > critical delta → 2 clicks, else 1 click.
        var clicks = distance > PeakReboundCriticalDelta ? 2 : 1;
        var direction = v < goodLo
            ? $"Speed up {what} rebound by {clicks} click{(clicks == 1 ? "" : "s")}"
            : $"Slow {what} rebound by {clicks} click{(clicks == 1 ? "" : "s")}";
        list.Add(new TuningSuggestion(sev, component,
            direction,
            Fmt($"Peak rebound speed {v:0} mm/s — target {goodLo:0}–{goodHi:0} mm/s"),
            Score(sev, 5)));
    }

    private static int Score(TuningSeverity s, int weight) => weight + (int)s * 3;

    private static string Fmt(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
