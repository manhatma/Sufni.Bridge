using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Svg.Skia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MathNet.Numerics.Statistics;
using Microsoft.Extensions.DependencyInjection;
using ScottPlot;
using Sufni.Bridge.Models;
using Sufni.Bridge.Models.Telemetry;
using Sufni.Bridge.Plots;
using Sufni.Bridge.Services;
using Sufni.Bridge.ViewModels.Items;

namespace Sufni.Bridge.ViewModels;

public class CompareTableRow
{
    public string Label { get; }
    public List<string> Values { get; }

    public CompareTableRow(string label, List<string> values)
    {
        Label = label;
        Values = values;
    }
}

public class CompareLegendEntry
{
    public string Name { get; }
    public string Color { get; }

    public CompareLegendEntry(string name, string color)
    {
        Name = name;
        Color = color;
    }
}

internal static class SessionSetupValues
{
    /// <summary>
    /// Renders one setup value for a session. A combined session inherits its whole setup
    /// verbatim from the chronologically first sub-session, so showing that single value would hide
    /// the fact that the sub-sessions ran different clicks — list every distinct leaf value instead.
    /// The selector already formats, so de-duplication happens on what the user actually sees.
    /// </summary>
    internal static string Get(SessionViewModel session, Func<Session, string?> selector)
    {
        if (!session.IsCombinedSession || session.SubSessions.Count == 0)
            return selector(session.SessionModel) ?? "-";

        var values = new List<string>();
        foreach (var leaf in LeafSessions(session).OrderBy(s => s.Timestamp ?? DateTime.MinValue))
        {
            if (selector(leaf.SessionModel) is { } value && !values.Contains(value))
                values.Add(value);
        }
        return values.Count > 0 ? string.Join(" / ", values) : "-";
    }

    private static IEnumerable<SessionViewModel> LeafSessions(SessionViewModel session)
    {
        if (!session.IsCombinedSession || session.SubSessions.Count == 0)
        {
            yield return session;
            yield break;
        }

        foreach (var subSession in session.SubSessions)
        foreach (var leaf in LeafSessions(subSession))
            yield return leaf;
    }
}

public partial class CompareSessionsViewModel : ViewModelBase
{
    private static readonly Color[] SessionColors =
    [
        Color.FromHex("#d53e4f"),  // Red
        Color.FromHex("#3288bd"),  // Blue
        Color.FromHex("#66c2a5"), // Green
    ];

    private static readonly LinePattern[] SessionPatterns =
    [
        LinePattern.Dashed,
        LinePattern.DenselyDashed,
        LinePattern.Dotted,
    ];

    public List<SessionViewModel> Sessions { get; }
    public List<string> SessionNames { get; }
    public List<CompareLegendEntry> SessionLegend { get; }
    public int SessionCount => Sessions.Count;

    [ObservableProperty] private SvgImage? frontTravelHistogramSvg;
    [ObservableProperty] private SvgImage? rearTravelHistogramSvg;
    [ObservableProperty] private SvgImage? frontRearTravelSvg;
    [ObservableProperty] private SvgImage? cumulativeTravelSvg;
    [ObservableProperty] private SvgImage? balanceSvg;
    [ObservableProperty] private SvgImage? reboundBalanceSvg;
    [ObservableProperty] private SvgImage? compressionBalanceSvg;
    [ObservableProperty] private SvgImage? frontVelocityHistogramSvg;
    [ObservableProperty] private SvgImage? rearVelocityHistogramSvg;
    [ObservableProperty] private SvgImage? frontLowSpeedSvg;
    [ObservableProperty] private SvgImage? rearLowSpeedSvg;
    [ObservableProperty] private SvgImage? rearDamperVelocityHistogramSvg;
    [ObservableProperty] private SvgImage? frontVelocitySpectrumSvg;
    [ObservableProperty] private SvgImage? rearVelocitySpectrumSvg;
    [ObservableProperty] private SvgImage? frontTravelSpectrumLowSvg;
    [ObservableProperty] private SvgImage? rearTravelSpectrumLowSvg;
    [ObservableProperty] private SvgImage? frontTravelSpectrumHighSvg;
    [ObservableProperty] private SvgImage? rearTravelSpectrumHighSvg;
    [ObservableProperty] private SvgImage? frontPositionVelocitySvg;
    [ObservableProperty] private SvgImage? rearPositionVelocitySvg;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportPdfCommand))]
    private bool isLoading = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportPdfCommand))]
    private bool isGeneratingPdf;

    // Raw SVG XML strings for PDF export
    private string? _frontTravelHistogramXml;
    private string? _rearTravelHistogramXml;
    private string? _frontRearTravelXml;
    private string? _cumulativeTravelXml;
    private string? _balanceXml;
    private string? _reboundBalanceXml;
    private string? _compressionBalanceXml;
    private string? _frontVelocityHistogramXml;
    private string? _rearVelocityHistogramXml;
    private string? _frontLowSpeedXml;
    private string? _rearLowSpeedXml;
    private string? _rearDamperVelocityHistogramXml;
    private string? _frontVelocitySpectrumXml;
    private string? _rearVelocitySpectrumXml;
    private string? _frontTravelSpectrumLowXml;
    private string? _rearTravelSpectrumLowXml;
    private string? _frontTravelSpectrumHighXml;
    private string? _rearTravelSpectrumHighXml;
    private string? _frontPositionVelocityXml;
    private string? _rearPositionVelocityXml;

    public ObservableCollection<CompareTableRow> FrontWheelRows { get; } = [];
    public ObservableCollection<CompareTableRow> RearWheelRows { get; } = [];
    public ObservableCollection<CompareTableRow> BalanceRows { get; } = [];

    public CompareSessionsViewModel(List<SessionViewModel> sessions)
    {
        Sessions = sessions.OrderBy(s => s.Timestamp ?? DateTime.MinValue).ToList();
        SessionNames = Sessions.Select(s => s.Name ?? "Unknown").ToList();
        SessionLegend = SessionNames
            .Select((name, index) => new CompareLegendEntry(name, FormatColor(SessionColors[index])))
            .ToList();
    }

    private static string FormatColor(Color color) => string.Create(
        CultureInfo.InvariantCulture, $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}");

    private static SvgSource? SvgToSource(string? svgXml) =>
        svgXml is null ? null : SvgSource.LoadFromSvg(svgXml);

    private static SvgImage? SourceToImage(SvgSource? source) =>
        source is null ? null : new SvgImage { Source = source };

    private static string FormatPercent(double value) =>
        string.Create(CultureInfo.InvariantCulture, $"{value:F1}");

    private static string FormatTravel(double value, double maxTravel) =>
        maxTravel <= 0
            ? "-"
            : string.Create(CultureInfo.InvariantCulture, $"{value / maxTravel * 100.0:0.0}");

    private static string FormatVelocity(double value) =>
        string.Create(CultureInfo.InvariantCulture, $"{value:0.0}");

    private static string FormatBottomouts(int value) => $"{value}";

    private sealed record SessionStats(
        DetailedTravelStatistics Travel,
        VelocityStatistics Velocity,
        double Comp95th,
        double Reb95th,
        double MaxTravel,
        VelocityBands? Bands);

    private static SessionStats? BuildSessionStats(TelemetryData data, SuspensionType type)
    {
        var suspension = type == SuspensionType.Front ? data.Front : data.Rear;
        if (!suspension.Present) return null;

        var maxTravel = type == SuspensionType.Front ? data.Linkage.MaxFrontTravel : data.Linkage.MaxRearTravel;
        var travel = data.CalculateDetailedTravelStatistics(type);
        var velocity = data.CalculateVelocityStatistics(type);
        var deadBand = type == SuspensionType.Front
            ? data.FrontVelocityDeadBand()
            : data.RearWheelVelocityDeadBand();
        var bands = data.CalculateVelocityBands(type, 200, deadBand);

        var compVels = suspension.Strokes.Compressions
            .SelectMany(s => suspension.Velocity[s.Start..(s.End + 1)])
            .ToList();
        var rebVels = suspension.Strokes.Rebounds
            .SelectMany(s => suspension.Velocity[s.Start..(s.End + 1)].Select(Math.Abs))
            .ToList();

        return new SessionStats(
            travel, velocity,
            compVels.Count > 0 ? compVels.Percentile(95) : 0.0,
            rebVels.Count > 0 ? -rebVels.Percentile(95) : 0.0,
            maxTravel, bands);
    }

    private static List<CompareTableRow> BuildSummaryRows(List<SessionStats?> statsList, List<SessionViewModel> sessions, SuspensionType type)
    {
        var rows = new List<CompareTableRow>
        {
            new("Spring", sessions.Select(s => SessionSetupValues.Get(s,
                model => type == SuspensionType.Front ? model.FrontSpringRate : model.RearSpringRate)).ToList()),
            new("VolSpc", sessions.Select(s => SessionSetupValues.Get(s,
                model => (type == SuspensionType.Front ? model.FrontVolSpc : model.RearVolSpc) is { } v
                    ? string.Create(CultureInfo.InvariantCulture, $"{v:F2}")
                    : null)).ToList()),
            new("HSC [clicks]", sessions.Select(s => SessionSetupValues.Get(s,
                model => Clicks(type == SuspensionType.Front ? model.FrontHighSpeedCompression : model.RearHighSpeedCompression))).ToList()),
            new("LSC [clicks]", sessions.Select(s => SessionSetupValues.Get(s,
                model => Clicks(type == SuspensionType.Front ? model.FrontLowSpeedCompression : model.RearLowSpeedCompression))).ToList()),
            new("LSR [clicks]", sessions.Select(s => SessionSetupValues.Get(s,
                model => Clicks(type == SuspensionType.Front ? model.FrontLowSpeedRebound : model.RearLowSpeedRebound))).ToList()),
            new("HSR [clicks]", sessions.Select(s => SessionSetupValues.Get(s,
                model => Clicks(type == SuspensionType.Front ? model.FrontHighSpeedRebound : model.RearHighSpeedRebound))).ToList()),
            new("Pos [AVG, %]", statsList.Select(s => s is null ? "-" : FormatTravel(s.Travel.Average, s.MaxTravel)).ToList()),
            new("Pos [95th, %]", statsList.Select(s => s is null ? "-" : FormatTravel(s.Travel.P95, s.MaxTravel)).ToList()),
            new("Pos [MAX, %]", statsList.Select(s => s is null ? "-" : FormatTravel(s.Travel.Max, s.MaxTravel)).ToList()),
            new("Bottom out [times]", statsList.Select(s => s is null ? "-" : FormatBottomouts(s.Travel.Bottomouts)).ToList()),
            new("Comp [AVG, mm/s]", statsList.Select(s => s is null ? "-" : FormatVelocity(s.Velocity.AverageCompression)).ToList()),
            new("Reb [AVG, mm/s]", statsList.Select(s => s is null ? "-" : FormatVelocity(s.Velocity.AverageRebound)).ToList()),
            new("Comp [95th, mm/s]", statsList.Select(s => s is null ? "-" : FormatVelocity(s.Comp95th)).ToList()),
            new("Reb [95th, mm/s]", statsList.Select(s => s is null ? "-" : FormatVelocity(s.Reb95th)).ToList()),
            new("Comp [MAX, mm/s]", statsList.Select(s => s is null ? "-" : FormatVelocity(s.Velocity.MaxCompression)).ToList()),
            new("Reb [MAX, mm/s]", statsList.Select(s => s is null ? "-" : FormatVelocity(s.Velocity.MaxRebound)).ToList()),
            new("HSR [%]", statsList.Select(s => s?.Bands is null ? "-" : FormatPercent(s.Bands.HighSpeedRebound)).ToList()),
            new("LSR [%]", statsList.Select(s => s?.Bands is null ? "-" : FormatPercent(s.Bands.LowSpeedRebound)).ToList()),
            new("LSC [%]", statsList.Select(s => s?.Bands is null ? "-" : FormatPercent(s.Bands.LowSpeedCompression)).ToList()),
            new("HSC [%]", statsList.Select(s => s?.Bands is null ? "-" : FormatPercent(s.Bands.HighSpeedCompression)).ToList()),
        };
        return rows;
    }

    private static string? Clicks(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture);

    private static string FormatBalanceValue(double? value, string format) =>
        value.HasValue ? value.Value.ToString(format, CultureInfo.InvariantCulture) : "-";

    private static string FormatBalanceCount(int? value) =>
        value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "-";

    private static List<CompareTableRow> BuildBalanceRows(List<BalanceMetrics> metricsList, List<TelemetryData> telemetry)
    {
        List<string> Values(Func<BalanceMetrics, string> formatter) =>
            metricsList.Select(formatter).ToList();

        var travelTotals = telemetry.Select(data =>
        {
            var front = data.Front.Present
                ? data.CalculateCumulativeTravel(SuspensionType.Front)
                : [];
            var rear = data.Rear.Present
                ? data.CalculateCumulativeTravel(SuspensionType.Rear)
                : [];
            var frontTotalM = front.Length > 0 ? front[^1] / 1000.0 : (double?)null;
            var rearTotalM = rear.Length > 0 ? rear[^1] / 1000.0 : (double?)null;
            var durationMinutes = data.SampleRate > 0
                ? Math.Max(front.Length, rear.Length) / (double)data.SampleRate / 60.0
                : 0.0;
            var rate = durationMinutes > 0 && (frontTotalM.HasValue || rearTotalM.HasValue)
                ? (frontTotalM.GetValueOrDefault() + rearTotalM.GetValueOrDefault()) / durationMinutes
                : (double?)null;
            return (frontTotalM, rearTotalM, rate);
        }).ToList();

        List<string> TravelValues(Func<(double? frontTotalM, double? rearTotalM, double? rate), double?> selector,
            string format) => travelTotals.Select(t => FormatBalanceValue(selector(t), format)).ToList();

        return
        [
            new("Front Wheel SAG (dyn.) [%]", Values(m => FormatBalanceValue(m.FrontSagPct, "0.0"))),
            new("Rear Wheel SAG (dyn.) [%]", Values(m => FormatBalanceValue(m.RearSagPct, "0.0"))),
            new("Sag-Diff |F−R| [pp]", Values(m => FormatBalanceValue(m.SagDifferencePp, "0.0"))),
            new("Damper SAG (dyn.) [%]", Values(m => FormatBalanceValue(m.DamperSagPct, "0.0"))),
            new("Front 95th [%]", Values(m => FormatBalanceValue(m.FrontP95Pct, "0.0"))),
            new("Rear 95th [%]", Values(m => FormatBalanceValue(m.RearP95Pct, "0.0"))),
            new("Bottom outs F / R", Values(m => $"{FormatBalanceCount(m.FrontBottomouts)} / {FormatBalanceCount(m.RearBottomouts)}")),
            new("Pitch μ [°]", Values(m => FormatBalanceValue(m.PitchMeanDeg, "0.00"))),
            new("Pitch stability σ [°]", Values(m => FormatBalanceValue(m.PitchStabilityDeg, "0.00"))),
            new("G-out asymmetry [%]", Values(m => m.GoutAsymmetryPct.HasValue
                ? $"{FormatBalanceValue(m.GoutAsymmetryPct, "0.0")} (N={FormatBalanceCount(m.GoutEventCount)})"
                : "-")),
            new("Comp vel ratio", Values(m => FormatBalanceValue(m.CompressionVelocityRatio, "0.000"))),
            new("Reb vel ratio", Values(m => FormatBalanceValue(m.ReboundVelocityRatio, "0.000"))),
            new("MSD Compression [%]", Values(m => FormatBalanceValue(m.CompressionMsd, "0.0"))),
            new("MSD Rebound [%]", Values(m => FormatBalanceValue(m.ReboundMsd, "0.0"))),
            new("Velocity shape β F / R", Values(m => $"{FormatBalanceValue(m.FrontVelocityShapeBeta, "0.00")} / {FormatBalanceValue(m.RearVelocityShapeBeta, "0.00")}")),
            new("Front freq [Hz]", Values(m => FormatBalanceValue(m.FrontPeakFrequencyHz, "0.00"))),
            new("Rear freq [Hz]", Values(m => FormatBalanceValue(m.RearPeakFrequencyHz, "0.00"))),
            new("Freq diff [Hz]", Values(m => FormatBalanceValue(m.FrequencyDifferenceHz, "0.00"))),
            new("Peak amp ratio", Values(m => FormatBalanceValue(m.PeakAmplitudeRatio, "0.000"))),
            new("Head angle static [°]", Values(m => FormatBalanceValue(m.HeadAngleStaticDeg, "0.0"))),
            new("Head angle shift [°]", Values(m => FormatBalanceValue(m.HeadAngleShiftDeg, "0.0"))),
            new("Cumulative travel F [m]", TravelValues(t => t.frontTotalM, "0")),
            new("Cumulative travel R [m]", TravelValues(t => t.rearTotalM, "0")),
            new("Travel rate [m/min]", TravelValues(t => t.rate, "0.0")),
        ];
    }

    public async Task GenerateComparePlots()
    {
        var databaseService = App.Current?.Services?.GetService<IDatabaseService>();
        Debug.Assert(databaseService != null, nameof(databaseService) + " != null");

        var bounds = SessionViewModel.LastKnownBounds;
        var (width, height) = ((int)bounds.Width, (int)(bounds.Height / 2.0));

        // Load TelemetryData for all sessions
        var sessionData = new List<(TelemetryData data, Color color, LinePattern pattern, string name)>();
        var cachedBalanceMetrics = new List<BalanceMetrics?>();
        for (var i = 0; i < Sessions.Count; i++)
        {
            var telemetry = await databaseService.GetSessionPsstAsync(Sessions[i].Id);
            if (telemetry is null)
            {
                ErrorMessages.Add($"Session '{Sessions[i].Name}' has no processed data.");
                continue;
            }

            sessionData.Add((telemetry, SessionColors[i], SessionPatterns[i], Sessions[i].Name ?? $"Session {i + 1}"));

            BalanceMetrics? metrics = null;
            var cacheMeta = await databaseService.GetSessionCacheMetaAsync(Sessions[i].Id);
            // A stale cache can silently lack fields added after it was written, so gate the
            // whole payload by version rather than checking individual deserialized fields.
            if (cacheMeta is { PlotVersion: SessionViewModel.CurrentPlotVersion, BalanceMetricsJson: not null })
            {
                try
                {
                    metrics = JsonSerializer.Deserialize<BalanceMetrics>(cacheMeta.BalanceMetricsJson);
                }
                catch (JsonException)
                {
                    // The metrics are recalculated below when cached JSON is invalid.
                }
            }
            cachedBalanceMetrics.Add(metrics);
        }

        if (sessionData.Count < 2)
        {
            ErrorMessages.Add("At least 2 sessions with processed data are required.");
            IsLoading = false;
            return;
        }

        // Generate plots in parallel
        var tasks = new List<Task>();

        // 1. Front Travel Histogram
        tasks.Add(Task.Run(() =>
        {
            var p = new CompareTravelHistogramPlot(new Plot(), SuspensionType.Front);
            p.LoadMultipleSessions(sessionData);
            var svg = p.Plot.GetSvgXml(width, height);
            _frontTravelHistogramXml = svg;
            var src = SvgToSource(svg);
            Dispatcher.UIThread.Post(() => FrontTravelHistogramSvg = SourceToImage(src));
        }));

        // 2. Rear Travel Histogram
        tasks.Add(Task.Run(() =>
        {
            var p = new CompareTravelHistogramPlot(new Plot(), SuspensionType.Rear);
            p.LoadMultipleSessions(sessionData);
            var svg = p.Plot.GetSvgXml(width, height);
            _rearTravelHistogramXml = svg;
            var src = SvgToSource(svg);
            Dispatcher.UIThread.Post(() => RearTravelHistogramSvg = SourceToImage(src));
        }));

        // 3. Front vs Rear Travel
        tasks.Add(Task.Run(() =>
        {
            var p = new CompareFrontRearTravelPlot(new Plot());
            p.LoadMultipleSessions(sessionData);
            var svg = p.Plot.GetSvgXml(width, height);
            _frontRearTravelXml = svg;
            var src = SvgToSource(svg);
            Dispatcher.UIThread.Post(() => FrontRearTravelSvg = SourceToImage(src));
        }));

        // 4. Cumulative Travel
        tasks.Add(Task.Run(() =>
        {
            var p = new CompareCumulativeTravelPlot(new Plot());
            p.LoadMultipleSessions(sessionData);
            var svg = p.Plot.GetSvgXml(width, height);
            _cumulativeTravelXml = svg;
            var src = SvgToSource(svg);
            Dispatcher.UIThread.Post(() => CumulativeTravelSvg = SourceToImage(src));
        }));

        // 5. Balance
        tasks.Add(Task.Run(() =>
        {
            var p = new CompareBalancePlot(new Plot());
            p.LoadMultipleSessions(sessionData);
            var svg = p.Plot.GetSvgXml(width, height);
            _balanceXml = svg;
            var src = SvgToSource(svg);
            Dispatcher.UIThread.Post(() => BalanceSvg = SourceToImage(src));
        }));

        // 5. Rebound Balance
        tasks.Add(Task.Run(() =>
        {
            var p = new CompareBalanceTypePlot(new Plot(), BalanceType.Rebound);
            p.LoadMultipleSessions(sessionData);
            var svg = p.Plot.GetSvgXml(width, height);
            _reboundBalanceXml = svg;
            var src = SvgToSource(svg);
            Dispatcher.UIThread.Post(() => ReboundBalanceSvg = SourceToImage(src));
        }));

        // 6. Compression Balance
        tasks.Add(Task.Run(() =>
        {
            var p = new CompareBalanceTypePlot(new Plot(), BalanceType.Compression);
            p.LoadMultipleSessions(sessionData);
            var svg = p.Plot.GetSvgXml(width, height);
            _compressionBalanceXml = svg;
            var src = SvgToSource(svg);
            Dispatcher.UIThread.Post(() => CompressionBalanceSvg = SourceToImage(src));
        }));

        // 7. Front Velocity Histogram (±2 m/s)
        tasks.Add(Task.Run(() =>
        {
            var p = new CompareVelocityHistogramPlot(new Plot(), SuspensionType.Front);
            p.LoadMultipleSessions(sessionData);
            var svg = p.Plot.GetSvgXml(width, height);
            _frontVelocityHistogramXml = svg;
            var src = SvgToSource(svg);
            Dispatcher.UIThread.Post(() => FrontVelocityHistogramSvg = SourceToImage(src));
        }));

        // 8. Rear Velocity Histogram (±2 m/s)
        tasks.Add(Task.Run(() =>
        {
            var p = new CompareVelocityHistogramPlot(new Plot(), SuspensionType.Rear);
            p.LoadMultipleSessions(sessionData);
            var svg = p.Plot.GetSvgXml(width, height);
            _rearVelocityHistogramXml = svg;
            var src = SvgToSource(svg);
            Dispatcher.UIThread.Post(() => RearVelocityHistogramSvg = SourceToImage(src));
        }));

        // 9. Front Low-Speed Velocity
        tasks.Add(Task.Run(() =>
        {
            var p = new CompareLowSpeedVelocityPlot(new Plot(), SuspensionType.Front);
            p.LoadMultipleSessions(sessionData);
            var svg = p.Plot.GetSvgXml(width, height);
            _frontLowSpeedXml = svg;
            var src = SvgToSource(svg);
            Dispatcher.UIThread.Post(() => FrontLowSpeedSvg = SourceToImage(src));
        }));

        // 6. Rear Low-Speed Velocity
        tasks.Add(Task.Run(() =>
        {
            var p = new CompareLowSpeedVelocityPlot(new Plot(), SuspensionType.Rear);
            p.LoadMultipleSessions(sessionData);
            var svg = p.Plot.GetSvgXml(width, height);
            _rearLowSpeedXml = svg;
            var src = SvgToSource(svg);
            Dispatcher.UIThread.Post(() => RearLowSpeedSvg = SourceToImage(src));
        }));

        // Rear Damper Velocity Histogram (damper domain, mm/s, rear-only)
        tasks.Add(Task.Run(() =>
        {
            var p = new CompareDamperVelocityHistogramPlot(new Plot());
            p.LoadMultipleSessions(sessionData);
            var svg = p.Plot.GetSvgXml(width, height);
            _rearDamperVelocityHistogramXml = svg;
            var src = SvgToSource(svg);
            Dispatcher.UIThread.Post(() => RearDamperVelocityHistogramSvg = SourceToImage(src));
        }));

        // 7. Front velocity spectrum (1–10 Hz, with body-resonance peak markers)
        tasks.Add(Task.Run(() =>
        {
            var p = new CompareSpectrumPlot(new Plot(), SuspensionType.Front,
                minHz: 1.0, maxHz: 10.0,
                peakMinHz: 1.3, peakMaxHz: 4.5,
                topHeadroomDb: 2.0,
                mode: WheelSpectrumMode.Velocity);
            p.LoadMultipleSessions(sessionData);
            var svg = p.Plot.GetSvgXml(width, height);
            _frontVelocitySpectrumXml = svg;
            var src = SvgToSource(svg);
            Dispatcher.UIThread.Post(() => FrontVelocitySpectrumSvg = SourceToImage(src));
        }));

        // 8. Rear velocity spectrum (1–10 Hz)
        tasks.Add(Task.Run(() =>
        {
            var p = new CompareSpectrumPlot(new Plot(), SuspensionType.Rear,
                minHz: 1.0, maxHz: 10.0,
                peakMinHz: 1.3, peakMaxHz: 4.5,
                topHeadroomDb: 2.0,
                mode: WheelSpectrumMode.Velocity);
            p.LoadMultipleSessions(sessionData);
            var svg = p.Plot.GetSvgXml(width, height);
            _rearVelocitySpectrumXml = svg;
            var src = SvgToSource(svg);
            Dispatcher.UIThread.Post(() => RearVelocitySpectrumSvg = SourceToImage(src));
        }));

        // 9. Front travel spectrum (1–10 Hz)
        tasks.Add(Task.Run(() =>
        {
            var p = new CompareSpectrumPlot(new Plot(), SuspensionType.Front,
                minHz: 1.0, maxHz: 10.0,
                peakMinHz: 1.3, peakMaxHz: 4.5,
                topHeadroomDb: 3.0);
            p.LoadMultipleSessions(sessionData);
            var svg = p.Plot.GetSvgXml(width, height);
            _frontTravelSpectrumLowXml = svg;
            var src = SvgToSource(svg);
            Dispatcher.UIThread.Post(() => FrontTravelSpectrumLowSvg = SourceToImage(src));
        }));

        // 10. Rear travel spectrum (1–10 Hz)
        tasks.Add(Task.Run(() =>
        {
            var p = new CompareSpectrumPlot(new Plot(), SuspensionType.Rear,
                minHz: 1.0, maxHz: 10.0,
                peakMinHz: 1.3, peakMaxHz: 4.5,
                topHeadroomDb: 3.0);
            p.LoadMultipleSessions(sessionData);
            var svg = p.Plot.GetSvgXml(width, height);
            _rearTravelSpectrumLowXml = svg;
            var src = SvgToSource(svg);
            Dispatcher.UIThread.Post(() => RearTravelSpectrumLowSvg = SourceToImage(src));
        }));

        // 11. Front travel spectrum (10–100 Hz, no peak markers)
        tasks.Add(Task.Run(() =>
        {
            var p = new CompareSpectrumPlot(new Plot(), SuspensionType.Front,
                minHz: 10.0, maxHz: 100.0,
                peakMinHz: 0.0, peakMaxHz: 0.0,
                segmentLength: 4096,
                topHeadroomDb: 3.0,
                lineWidth: 1.5f);
            p.LoadMultipleSessions(sessionData);
            var svg = p.Plot.GetSvgXml(width, height);
            _frontTravelSpectrumHighXml = svg;
            var src = SvgToSource(svg);
            Dispatcher.UIThread.Post(() => FrontTravelSpectrumHighSvg = SourceToImage(src));
        }));

        // 12. Rear travel spectrum (10–100 Hz)
        tasks.Add(Task.Run(() =>
        {
            var p = new CompareSpectrumPlot(new Plot(), SuspensionType.Rear,
                minHz: 10.0, maxHz: 100.0,
                peakMinHz: 0.0, peakMaxHz: 0.0,
                segmentLength: 4096,
                topHeadroomDb: 3.0,
                lineWidth: 1.5f);
            p.LoadMultipleSessions(sessionData);
            var svg = p.Plot.GetSvgXml(width, height);
            _rearTravelSpectrumHighXml = svg;
            var src = SvgToSource(svg);
            Dispatcher.UIThread.Post(() => RearTravelSpectrumHighSvg = SourceToImage(src));
        }));

        // Front Position vs Velocity (phase portrait)
        tasks.Add(Task.Run(() =>
        {
            var p = new ComparePositionVelocityPlot(new Plot(), SuspensionType.Front);
            p.LoadMultipleSessions(sessionData);
            var svg = p.Plot.GetSvgXml(width, height);
            _frontPositionVelocityXml = svg;
            var src = SvgToSource(svg);
            Dispatcher.UIThread.Post(() => FrontPositionVelocitySvg = SourceToImage(src));
        }));

        // Rear Position vs Velocity (phase portrait)
        tasks.Add(Task.Run(() =>
        {
            var p = new ComparePositionVelocityPlot(new Plot(), SuspensionType.Rear);
            p.LoadMultipleSessions(sessionData);
            var svg = p.Plot.GetSvgXml(width, height);
            _rearPositionVelocityXml = svg;
            var src = SvgToSource(svg);
            Dispatcher.UIThread.Post(() => RearPositionVelocitySvg = SourceToImage(src));
        }));

        // Summary Tables
        tasks.Add(Task.Run(() =>
        {
            var frontStatsList = sessionData.Select(s => BuildSessionStats(s.data, SuspensionType.Front)).ToList();
            var rearStatsList = sessionData.Select(s => BuildSessionStats(s.data, SuspensionType.Rear)).ToList();
            var balanceMetrics = sessionData.Select((session, index) =>
                cachedBalanceMetrics[index] ?? session.data.CalculateBalanceMetrics(null)).ToList();

            var frontRows = BuildSummaryRows(frontStatsList, Sessions, SuspensionType.Front);
            var rearRows = BuildSummaryRows(rearStatsList, Sessions, SuspensionType.Rear);
            var balanceRows = BuildBalanceRows(balanceMetrics, sessionData.Select(s => s.data).ToList());

            Dispatcher.UIThread.Post(() =>
            {
                foreach (var row in frontRows) FrontWheelRows.Add(row);
                foreach (var row in rearRows) RearWheelRows.Add(row);
                foreach (var row in balanceRows) BalanceRows.Add(row);
            });
        }));

        await Task.WhenAll(tasks);

        await Dispatcher.UIThread.InvokeAsync(() => IsLoading = false);
    }

    private bool CanExportPdf() => !IsLoading && !IsGeneratingPdf &&
        (_frontTravelHistogramXml is not null || _rearTravelHistogramXml is not null ||
         _frontRearTravelXml is not null || _cumulativeTravelXml is not null || _balanceXml is not null ||
         _reboundBalanceXml is not null || _compressionBalanceXml is not null ||
         _frontVelocityHistogramXml is not null || _rearVelocityHistogramXml is not null ||
         _frontLowSpeedXml is not null || _rearLowSpeedXml is not null ||
         _frontVelocitySpectrumXml is not null || _rearVelocitySpectrumXml is not null ||
         _frontTravelSpectrumLowXml is not null || _rearTravelSpectrumLowXml is not null ||
         _frontTravelSpectrumHighXml is not null || _rearTravelSpectrumHighXml is not null ||
         _rearDamperVelocityHistogramXml is not null || _frontPositionVelocityXml is not null ||
         _rearPositionVelocityXml is not null);

    [RelayCommand(CanExecute = nameof(CanExportPdf))]
    private async Task ExportPdf()
    {
        IsGeneratingPdf = true;
        try
        {
            var svgs = new List<string?> {
                _frontTravelHistogramXml, _rearTravelHistogramXml,
                _frontRearTravelXml, _cumulativeTravelXml,
                _balanceXml, _reboundBalanceXml, _compressionBalanceXml,
                _frontVelocityHistogramXml, _rearVelocityHistogramXml,
                _frontLowSpeedXml, _rearLowSpeedXml,
                _rearDamperVelocityHistogramXml,
                _frontVelocitySpectrumXml, _rearVelocitySpectrumXml,
                _frontTravelSpectrumLowXml, _rearTravelSpectrumLowXml,
                _frontTravelSpectrumHighXml, _rearTravelSpectrumHighXml,
                _frontPositionVelocityXml, _rearPositionVelocityXml,
            };
            var validSvgs = svgs.Where(s => s is not null).Cast<string>().ToList();
            if (validSvgs.Count == 0)
            {
                ErrorMessages.Add("No plots to export.");
                return;
            }

            var sessionNames = string.Join("_vs_", SessionNames.Select(n =>
                System.Text.RegularExpressions.Regex.Replace(n, @"[^\w\-.]", "_")));
            var legend = SessionLegend.ToList();
            var frontRows = FrontWheelRows.ToList();
            var rearRows = RearWheelRows.ToList();
            var pdfPath = await Task.Run(() =>
                RenderSvgsToPdf(validSvgs, sessionNames, legend, frontRows, rearRows));

            IsGeneratingPdf = false;
            var shareService = App.Current?.Services?.GetService<IShareService>();
            if (shareService is not null)
                await shareService.ShareFileAsync(pdfPath);
        }
        catch (Exception e)
        {
            IsGeneratingPdf = false;
            ErrorMessages.Add($"PDF export failed: {e.Message}");
        }
    }

    private static string RenderSvgsToPdf(
        List<string> svgXmlList,
        string fileName,
        List<CompareLegendEntry> legend,
        List<CompareTableRow> frontRows,
        List<CompareTableRow> rearRows)
    {
        var tempDir = System.IO.Path.GetTempPath();
        var pdfPath = System.IO.Path.Combine(tempDir, $"{fileName}.pdf");

        var svgObjects = svgXmlList
            .AsParallel()
            .AsOrdered()
            .Select(xml =>
            {
                var svg = new Svg.Skia.SKSvg();
                svg.FromSvg(xml);
                return svg;
            })
            .ToList();

        try
        {
            using var stream = new System.IO.FileStream(pdfPath, System.IO.FileMode.Create);
            using var document = SkiaSharp.SKDocument.CreatePdf(stream);

            var firstPicture = svgObjects.Select(svg => svg.Picture).FirstOrDefault(picture => picture is not null);
            var pageWidth = firstPicture?.CullRect.Width ?? 400f;
            var plotPageHeight = firstPicture?.CullRect.Height ?? 560f;
            DrawOverviewPages(document, pageWidth, plotPageHeight, legend, frontRows, rearRows);

            foreach (var svg in svgObjects)
            {
                var picture = svg.Picture;
                if (picture is null) continue;

                var bounds = picture.CullRect;
                using var canvas = document.BeginPage(bounds.Width, bounds.Height);
                canvas.DrawPicture(picture);
                document.EndPage();
            }

            document.Close();
        }
        finally
        {
            foreach (var svg in svgObjects)
                svg.Dispose();
        }

        return pdfPath;
    }

    private static void DrawOverviewPages(
        SkiaSharp.SKDocument document,
        float pageWidth,
        float plotPageHeight,
        List<CompareLegendEntry> legend,
        List<CompareTableRow> frontRows,
        List<CompareTableRow> rearRows)
    {
        const float margin = 24f;
        const float legendRowHeight = 18f;
        const float tableRowHeight = 24f;
        const float sectionGap = 14f;
        const float labelColumnWidth = 130f;

        var naturalHeight = margin * 2f
            + legend.Count * legendRowHeight
            + sectionGap * 2f
            + (frontRows.Count + rearRows.Count + 2) * tableRowHeight;
        var pageHeight = Math.Max(plotPageHeight, Math.Min(naturalHeight, pageWidth * 1.4142f));
        var contentWidth = pageWidth - margin * 2f;
        var valueColumnWidth = legend.Count > 0
            ? Math.Max(1f, (contentWidth - labelColumnWidth) / legend.Count)
            : contentWidth - labelColumnWidth;

        var background = SkiaSharp.SKColor.Parse("#15191C");
        var cellBackground = SkiaSharp.SKColor.Parse("#20262B");
        var headerBackground = SkiaSharp.SKColor.Parse("#66C2A5");
        var lightText = SkiaSharp.SKColor.Parse("#D0D0D0");
        var darkText = SkiaSharp.SKColor.Parse("#15191C");
        var border = SkiaSharp.SKColor.Parse("#505558");

        using var fillPaint = new SkiaSharp.SKPaint { IsStroke = false };
        using var borderPaint = new SkiaSharp.SKPaint
        {
            IsStroke = true,
            StrokeWidth = 0.75f,
            Color = border,
        };
        using var textPaint = new SkiaSharp.SKPaint
        {
            IsAntialias = true,
            TextSize = 11f,
        };
        using var boldPaint = new SkiaSharp.SKPaint
        {
            IsAntialias = true,
            TextSize = 10f,
            Typeface = SkiaSharp.SKTypeface.FromFamilyName(null, SkiaSharp.SKFontStyle.Bold),
        };

        SkiaSharp.SKCanvas? canvas = null;
        var y = margin;

        void BeginPage()
        {
            canvas = document.BeginPage(pageWidth, pageHeight);
            canvas.Clear(background);
            y = margin;
        }

        void EndPage()
        {
            var completedCanvas = canvas;
            document.EndPage();
            canvas = null;
            completedCanvas?.Dispose();
        }

        void DrawCell(float x, float top, float width, string text, bool header, bool rightAlign)
        {
            var rect = new SkiaSharp.SKRect(x, top, x + width, top + tableRowHeight);
            fillPaint.Color = header ? headerBackground : cellBackground;
            canvas!.DrawRect(rect, fillPaint);
            canvas.DrawRect(rect, borderPaint);

            var paint = header ? boldPaint : textPaint;
            paint.Color = header ? darkText : lightText;
            var textWidth = paint.MeasureText(text);
            var textX = rightAlign ? x + width - 6f - textWidth : x + 6f;
            var textY = top + tableRowHeight / 2f + paint.TextSize * 0.35f;
            canvas.DrawText(text, textX, textY, paint);
        }

        void DrawTableHeader(string title)
        {
            DrawCell(margin, y, labelColumnWidth, title, true, false);
            for (var i = 0; i < legend.Count; i++)
            {
                DrawCell(
                    margin + labelColumnWidth + i * valueColumnWidth,
                    y,
                    valueColumnWidth,
                    legend[i].Name,
                    true,
                    true);
            }
            y += tableRowHeight;
        }

        void DrawTable(string title, List<CompareTableRow> rows)
        {
            // Mirrors the on-screen tables, which are hidden when there are no rows.
            if (rows.Count == 0) return;

            if (y + tableRowHeight > pageHeight - margin)
            {
                EndPage();
                BeginPage();
            }

            DrawTableHeader(title);
            foreach (var row in rows)
            {
                if (y + tableRowHeight > pageHeight - margin)
                {
                    EndPage();
                    BeginPage();
                    DrawTableHeader(title);
                }

                DrawCell(margin, y, labelColumnWidth, row.Label, false, false);
                for (var i = 0; i < legend.Count; i++)
                {
                    var value = i < row.Values.Count ? row.Values[i] : "-";
                    DrawCell(
                        margin + labelColumnWidth + i * valueColumnWidth,
                        y,
                        valueColumnWidth,
                        value,
                        false,
                        true);
                }
                y += tableRowHeight;
            }
        }

        BeginPage();

        foreach (var entry in legend)
        {
            var color = SkiaSharp.SKColor.Parse(entry.Color);
            fillPaint.Color = color;
            canvas!.DrawRect(new SkiaSharp.SKRect(margin, y + 7f, margin + 18f, y + 10f), fillPaint);
            textPaint.Color = color;
            canvas.DrawText(entry.Name, margin + 25f, y + 12f, textPaint);
            y += legendRowHeight;
        }

        y += sectionGap;
        DrawTable("FRONT WHEEL", frontRows);
        y += sectionGap;
        DrawTable("REAR WHEEL", rearRows);
        EndPage();
    }
}
