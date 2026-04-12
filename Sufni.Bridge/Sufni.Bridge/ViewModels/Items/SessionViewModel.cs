using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using MathNet.Numerics.Statistics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Svg.Skia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using ScottPlot;
using Sufni.Bridge.Models;
using Sufni.Bridge.Models.Telemetry;
using Sufni.Bridge.Plots;
using HapticFeedback;
using Sufni.Bridge.Services;
using Sufni.Bridge.ViewModels.SessionPages;

namespace Sufni.Bridge.ViewModels.Items;

public partial class SessionViewModel : ItemViewModelBase
{
    // Increment when plot visuals change to force cache regeneration on all sessions.
    private const int CurrentPlotVersion = 59;

    // Limits concurrent plot generation tasks to reduce peak memory on iOS.
    private static readonly SemaphoreSlim s_plotSemaphore = new(3, 3);

    // Shared across all instances — updated whenever any session loads with real bounds.
    // Default matches iPhone 15 Pro logical width; height/2 is used for plots.
    internal static Rect LastKnownBounds = new Rect(0, 0, 393, 700);

    private Session session;
    internal Session SessionModel => session;
    public bool IsInDatabase;
    private SpringPageViewModel SpringPage { get; } = new();
    private DamperPageViewModel DamperPage { get; } = new();
    private BalancePageViewModel BalancePage { get; } = new();
    private MiscPageViewModel MiscPage { get; } = new();
    private SummaryPageViewModel SummaryPage { get; } = new();
    private NotesPageViewModel NotesPage { get; } = new();
    public ObservableCollection<PageViewModelBase> Pages { get; }
    public string Description => NotesPage.Description ?? "";
    public override bool IsComplete => session.HasProcessedData;
    public override bool ShowPdfExportButton => true;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool isGeneratingPdf;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool isAnalyzingData;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool isCombinedSession;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool isExpanded;
    public ObservableCollection<SessionViewModel> SubSessions { get; } = [];

    public int NestingDepth => IsCombinedSession && SubSessions.Count > 0
        ? SubSessions.Max(s => s.NestingDepth) + 1
        : 1;

    [RelayCommand]
    private void ToggleExpand()
    {
        if (!IsCombinedSession) return;
        IsExpanded = !IsExpanded;
    }

    #region Private methods

    // Call on background thread — SvgSource : Object, thread-safe
    private static SvgSource? SvgToSource(string? svgXml) =>
        svgXml is null ? null : SvgSource.LoadFromSvg(svgXml);

    // Call on UI thread — SvgImage : AvaloniaObject, requires UI thread
    private static SvgImage? SourceToImage(SvgSource? source) =>
        source is null ? null : new SvgImage { Source = source };

    // Returns (cacheFound, hasVdc, hasPvc) so the caller can detect incomplete old caches
    // without checking in-memory properties that lazy loading hasn't set yet.
    private async Task<(bool found, bool hasVdc, bool hasPvc)> LoadCache()
    {
        var databaseService = App.Current?.Services?.GetService<IDatabaseService>();
        Debug.Assert(databaseService != null, nameof(databaseService) + " != null");

        var cache = await databaseService.GetSessionCacheAsync(Id);
        Debug.WriteLine($"Session {Id}: LoadCache - cache found={cache is not null}");
        if (cache is null || cache.PlotVersion != CurrentPlotVersion)
        {
            return (false, false, false);
        }

        var hasVdc = cache.VelocityDistributionComparison is not null;
        var hasPvc = cache.PositionVelocityComparison is not null;

        // 1. Summary: pure JSON, no SVG parsing — populate immediately
        if (cache.SummaryJson is not null)
        {
            try
            {
                var summary = JsonSerializer.Deserialize<CachedSummaryData>(cache.SummaryJson);
                if (summary is not null)
                {
                    SummaryPage.RunDataRows = new ObservableCollection<SummaryValueRow>(
                        summary.RunDataRows.Select(r => new SummaryValueRow(r[0], r[1])));
                    SummaryPage.ForkShockRows = new ObservableCollection<SummaryComparisonRow>(
                        summary.ForkShockRows.Select(r => new SummaryComparisonRow(r[0], r[1], r[2])));
                    SummaryPage.WheelRows = new ObservableCollection<SummaryComparisonRow>(
                        summary.WheelRows.Select(r => new SummaryComparisonRow(r[0], r[1], r[2])));
                }
            }
            catch
            {
                // Ignore corrupt summary cache - will be rebuilt from DB
            }
        }

        // 2. SpringPage SVGs: parse in parallel and await — first page with plots the user will see
        var travelCompTask       = Task.Run(() => SvgToSource(cache.TravelComparisonHistogram));
        var frontRearScatterTask = Task.Run(() => SvgToSource(cache.FrontRearTravelScatter));
        var frontTravelHistTask  = Task.Run(() => SvgToSource(cache.FrontTravelHistogram));
        var rearTravelHistTask   = Task.Run(() => SvgToSource(cache.RearTravelHistogram));

        await Task.WhenAll(travelCompTask, frontRearScatterTask, frontTravelHistTask, rearTravelHistTask);

        // SvgImage requires UI thread — Loaded command always runs on UI thread
        SpringPage.TravelComparisonHistogram = SourceToImage(travelCompTask.Result);
        SpringPage.FrontRearTravelScatter    = SourceToImage(frontRearScatterTask.Result);
        SpringPage.FrontTravelHistogram      = SourceToImage(frontTravelHistTask.Result);
        SpringPage.RearTravelHistogram       = SourceToImage(rearTravelHistTask.Result);

        // 3. Remaining pages: parse in background, only when cache is complete.
        //    Incomplete caches have hasVdc/hasPvc=false → caller triggers CreateCache() instead.
        if (hasVdc && hasPvc)
        {
            _ = Task.Run(async () =>
            {
                var frontVelHistTask   = Task.Run(() => SvgToSource(cache.FrontVelocityHistogram));
                var frontLsVelHistTask = Task.Run(() => SvgToSource(cache.FrontLowSpeedVelocityHistogram));
                var rearVelHistTask    = Task.Run(() => SvgToSource(cache.RearVelocityHistogram));
                var rearLsVelHistTask  = Task.Run(() => SvgToSource(cache.RearLowSpeedVelocityHistogram));
                var combBalTask      = Task.Run(() => SvgToSource(cache.CombinedBalance));
                var compBalTask      = Task.Run(() => SvgToSource(cache.CompressionBalance));
                var rebBalTask       = Task.Run(() => SvgToSource(cache.ReboundBalance));
                var velDistCompTask  = Task.Run(() => SvgToSource(cache.VelocityDistributionComparison));
                var posVelCompTask   = Task.Run(() => SvgToSource(cache.PositionVelocityComparison));
                var frontPosVelTask  = Task.Run(() => SvgToSource(cache.FrontPositionVelocity));
                var rearPosVelTask   = Task.Run(() => SvgToSource(cache.RearPositionVelocity));

                await Task.WhenAll(frontVelHistTask, frontLsVelHistTask, rearVelHistTask, rearLsVelHistTask,
                    combBalTask, compBalTask, rebBalTask,
                    velDistCompTask, posVelCompTask, frontPosVelTask, rearPosVelTask);

                var frontVelHistSrc   = frontVelHistTask.Result;
                var frontLsVelHistSrc = frontLsVelHistTask.Result;
                var rearVelHistSrc    = rearVelHistTask.Result;
                var rearLsVelHistSrc  = rearLsVelHistTask.Result;
                var combBalSrc      = combBalTask.Result;
                var compBalSrc      = compBalTask.Result;
                var rebBalSrc       = rebBalTask.Result;
                var velDistCompSrc  = velDistCompTask.Result;
                var posVelCompSrc   = posVelCompTask.Result;
                var frontPosVelSrc  = frontPosVelTask.Result;
                var rearPosVelSrc   = rearPosVelTask.Result;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    DamperPage.FrontVelocityHistogram          = SourceToImage(frontVelHistSrc);
                    DamperPage.FrontLowSpeedVelocityHistogram = SourceToImage(frontLsVelHistSrc);
                    DamperPage.RearVelocityHistogram           = SourceToImage(rearVelHistSrc);
                    DamperPage.RearLowSpeedVelocityHistogram  = SourceToImage(rearLsVelHistSrc);
                    DamperPage.FrontHscPercentage     = cache.FrontHscPercentage;
                    DamperPage.RearHscPercentage      = cache.RearHscPercentage;
                    DamperPage.FrontLscPercentage     = cache.FrontLscPercentage;
                    DamperPage.RearLscPercentage      = cache.RearLscPercentage;
                    DamperPage.FrontLsrPercentage     = cache.FrontLsrPercentage;
                    DamperPage.RearLsrPercentage      = cache.RearLsrPercentage;
                    DamperPage.FrontHsrPercentage     = cache.FrontHsrPercentage;
                    DamperPage.RearHsrPercentage      = cache.RearHsrPercentage;

                    if (compBalSrc is not null)
                    {
                        BalancePage.CombinedBalance    = SourceToImage(combBalSrc);
                        BalancePage.CompressionBalance = SourceToImage(compBalSrc);
                        BalancePage.ReboundBalance     = SourceToImage(rebBalSrc);
                    }
                    else
                    {
                        Pages.Remove(BalancePage);
                    }

                    DamperPage.VelocityDistributionComparison = SourceToImage(velDistCompSrc);
                    MiscPage.PositionVelocityComparison       = SourceToImage(posVelCompSrc);
                    MiscPage.FrontPositionVelocity            = SourceToImage(frontPosVelSrc);
                    MiscPage.RearPositionVelocity             = SourceToImage(rearPosVelSrc);
                });
            });
        }

        return (true, hasVdc, hasPvc);
    }

    private static Task ThrottledPlotTask(Action work)
    {
        return Task.Run(async () =>
        {
            await s_plotSemaphore.WaitAsync();
            try { work(); }
            finally { s_plotSemaphore.Release(); }
        });
    }

    private async Task CreateCache(object? bounds, TelemetryData telemetryData)
    {
        var databaseService = App.Current?.Services?.GetService<IDatabaseService>();
        Debug.Assert(databaseService != null, nameof(databaseService) + " != null");

        var b = (Rect)bounds!;
        var (width, height) = ((int)b.Width, (int)(b.Height / 2.0));

        var sessionCache = new SessionCache { SessionId = Id, PlotVersion = CurrentPlotVersion };
        var tasks = new List<Task>();

        // Shared VelocityBands tasks — computed once, used by both DamperPage UI and summary
        Task<VelocityBands?> frontBandsTask = Task.FromResult<VelocityBands?>(null);
        Task<VelocityBands?> rearBandsTask = Task.FromResult<VelocityBands?>(null);

        if (telemetryData.Front.Present)
        {
            frontBandsTask = Task.Run(() =>
                (VelocityBands?)telemetryData.CalculateVelocityBands(SuspensionType.Front, 200));
        }

        if (telemetryData.Rear.Present)
        {
            rearBandsTask = Task.Run(() =>
                (VelocityBands?)telemetryData.CalculateVelocityBands(SuspensionType.Rear, 200));
        }

        // Spring comparison plots (Front+Rear)
        if (telemetryData.Front.Present && telemetryData.Rear.Present)
        {
            tasks.Add(ThrottledPlotTask(() =>
            {
                var tcmp = new TravelHistogramComparisonPlot(new Plot());
                tcmp.LoadTelemetryData(telemetryData);
                sessionCache.TravelComparisonHistogram = tcmp.Plot.GetSvgXml(width, height);
                var travelCompSrc = SvgToSource(sessionCache.TravelComparisonHistogram);
                Dispatcher.UIThread.Post(() => { SpringPage.TravelComparisonHistogram = SourceToImage(travelCompSrc); });
            }));

            tasks.Add(ThrottledPlotTask(() =>
            {
                var frs = new FrontRearTravelScatterPlot(new Plot());
                frs.LoadTelemetryData(telemetryData);
                sessionCache.FrontRearTravelScatter = frs.Plot.GetSvgXml(width, height);
                var frontRearScatterSrc = SvgToSource(sessionCache.FrontRearTravelScatter);
                Dispatcher.UIThread.Post(() => { SpringPage.FrontRearTravelScatter = SourceToImage(frontRearScatterSrc); });
            }));
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                SpringPage.TravelComparisonHistogram = null;
                SpringPage.FrontRearTravelScatter = null;
            });
        }

        // Front plots — each in its own task
        if (telemetryData.Front.Present)
        {
            tasks.Add(ThrottledPlotTask(() =>
            {
                var fth = new TravelHistogramPlot(new Plot(), SuspensionType.Front);
                fth.LoadTelemetryData(telemetryData);
                sessionCache.FrontTravelHistogram = fth.Plot.GetSvgXml(width, height);
                var frontTravelHistSrc = SvgToSource(sessionCache.FrontTravelHistogram);
                Dispatcher.UIThread.Post(() => { SpringPage.FrontTravelHistogram = SourceToImage(frontTravelHistSrc); });
            }));

            tasks.Add(ThrottledPlotTask(() =>
            {
                var fvh = new VelocityHistogramPlot(new Plot(), SuspensionType.Front);
                fvh.LoadTelemetryData(telemetryData);
                sessionCache.FrontVelocityHistogram = fvh.Plot.GetSvgXml(width, height);
                var frontVelocityHistSrc = SvgToSource(sessionCache.FrontVelocityHistogram);
                Dispatcher.UIThread.Post(() => { DamperPage.FrontVelocityHistogram = SourceToImage(frontVelocityHistSrc); });
            }));

            tasks.Add(ThrottledPlotTask(() =>
            {
                var flsvh = new LowSpeedVelocityHistogramPlot(new Plot(), SuspensionType.Front);
                flsvh.LoadTelemetryData(telemetryData);
                sessionCache.FrontLowSpeedVelocityHistogram = flsvh.Plot.GetSvgXml(width, height);
                var frontLsVelHistSrc = SvgToSource(sessionCache.FrontLowSpeedVelocityHistogram);
                Dispatcher.UIThread.Post(() => { DamperPage.FrontLowSpeedVelocityHistogram = SourceToImage(frontLsVelHistSrc); });
            }));

            // Apply shared front VelocityBands to cache + UI
            tasks.Add(frontBandsTask.ContinueWith(t =>
            {
                var fvb = t.Result;
                if (fvb is null) return;
                sessionCache.FrontHsrPercentage = fvb.HighSpeedRebound;
                sessionCache.FrontLsrPercentage = fvb.LowSpeedRebound;
                sessionCache.FrontLscPercentage = fvb.LowSpeedCompression;
                sessionCache.FrontHscPercentage = fvb.HighSpeedCompression;
                Dispatcher.UIThread.Post(() =>
                {
                    DamperPage.FrontHsrPercentage = fvb.HighSpeedRebound;
                    DamperPage.FrontLsrPercentage = fvb.LowSpeedRebound;
                    DamperPage.FrontLscPercentage = fvb.LowSpeedCompression;
                    DamperPage.FrontHscPercentage = fvb.HighSpeedCompression;
                });
            }, TaskScheduler.Default));
        }

        // Rear plots — each in its own task
        if (telemetryData.Rear.Present)
        {
            tasks.Add(ThrottledPlotTask(() =>
            {
                var rth = new TravelHistogramPlot(new Plot(), SuspensionType.Rear);
                rth.LoadTelemetryData(telemetryData);
                sessionCache.RearTravelHistogram = rth.Plot.GetSvgXml(width, height);
                var rearTravelHistSrc = SvgToSource(sessionCache.RearTravelHistogram);
                Dispatcher.UIThread.Post(() => { SpringPage.RearTravelHistogram = SourceToImage(rearTravelHistSrc); });
            }));

            tasks.Add(ThrottledPlotTask(() =>
            {
                var rvh = new VelocityHistogramPlot(new Plot(), SuspensionType.Rear);
                rvh.LoadTelemetryData(telemetryData);
                sessionCache.RearVelocityHistogram = rvh.Plot.GetSvgXml(width, height);
                var rearVelocityHistSrc = SvgToSource(sessionCache.RearVelocityHistogram);
                Dispatcher.UIThread.Post(() => { DamperPage.RearVelocityHistogram = SourceToImage(rearVelocityHistSrc); });
            }));

            tasks.Add(ThrottledPlotTask(() =>
            {
                var rlsvh = new LowSpeedVelocityHistogramPlot(new Plot(), SuspensionType.Rear);
                rlsvh.LoadTelemetryData(telemetryData);
                sessionCache.RearLowSpeedVelocityHistogram = rlsvh.Plot.GetSvgXml(width, height);
                var rearLsVelHistSrc = SvgToSource(sessionCache.RearLowSpeedVelocityHistogram);
                Dispatcher.UIThread.Post(() => { DamperPage.RearLowSpeedVelocityHistogram = SourceToImage(rearLsVelHistSrc); });
            }));

            // Apply shared rear VelocityBands to cache + UI
            tasks.Add(rearBandsTask.ContinueWith(t =>
            {
                var rvb = t.Result;
                if (rvb is null) return;
                sessionCache.RearHsrPercentage = rvb.HighSpeedRebound;
                sessionCache.RearLsrPercentage = rvb.LowSpeedRebound;
                sessionCache.RearLscPercentage = rvb.LowSpeedCompression;
                sessionCache.RearHscPercentage = rvb.HighSpeedCompression;
                Dispatcher.UIThread.Post(() =>
                {
                    DamperPage.RearHsrPercentage = rvb.HighSpeedRebound;
                    DamperPage.RearLsrPercentage = rvb.LowSpeedRebound;
                    DamperPage.RearLscPercentage = rvb.LowSpeedCompression;
                    DamperPage.RearHscPercentage = rvb.HighSpeedCompression;
                });
            }, TaskScheduler.Default));
        }

        // Balance plots — each in its own task
        if (telemetryData.Front.Present && telemetryData.Rear.Present)
        {
            tasks.Add(ThrottledPlotTask(() =>
            {
                var combined = new CombinedBalancePlot(new Plot());
                combined.LoadTelemetryData(telemetryData);
                sessionCache.CombinedBalance = combined.Plot.GetSvgXml(width, height);
                var combinedBalanceSrc = SvgToSource(sessionCache.CombinedBalance);
                Dispatcher.UIThread.Post(() => { BalancePage.CombinedBalance = SourceToImage(combinedBalanceSrc); });
            }));

            tasks.Add(ThrottledPlotTask(() =>
            {
                var cb = new BalancePlot(new Plot(), BalanceType.Compression);
                cb.LoadTelemetryData(telemetryData);
                sessionCache.CompressionBalance = cb.Plot.GetSvgXml(width, height);
                var compressionBalanceSrc = SvgToSource(sessionCache.CompressionBalance);
                Dispatcher.UIThread.Post(() => { BalancePage.CompressionBalance = SourceToImage(compressionBalanceSrc); });
            }));

            tasks.Add(ThrottledPlotTask(() =>
            {
                var rb = new BalancePlot(new Plot(), BalanceType.Rebound);
                rb.LoadTelemetryData(telemetryData);
                sessionCache.ReboundBalance = rb.Plot.GetSvgXml(width, height);
                var reboundBalanceSrc = SvgToSource(sessionCache.ReboundBalance);
                Dispatcher.UIThread.Post(() => { BalancePage.ReboundBalance = SourceToImage(reboundBalanceSrc); });
            }));
        }
        else
        {
            Dispatcher.UIThread.Post(() => { Pages.Remove(BalancePage); });
        }

        // Misc plots — each in its own task
        tasks.Add(ThrottledPlotTask(() =>
        {
            var vdc = new VelocityDistributionComparisonPlot(new Plot());
            vdc.LoadTelemetryData(telemetryData);
            sessionCache.VelocityDistributionComparison = vdc.Plot.GetSvgXml(width, height);
            var velDistCompSrc = SvgToSource(sessionCache.VelocityDistributionComparison);
            Dispatcher.UIThread.Post(() => { DamperPage.VelocityDistributionComparison = SourceToImage(velDistCompSrc); });
        }));

        tasks.Add(ThrottledPlotTask(() =>
        {
            var pvc = new PositionVelocityComparisonPlot(new Plot());
            pvc.LoadTelemetryData(telemetryData);
            sessionCache.PositionVelocityComparison = pvc.Plot.GetSvgXml(width, height);
            var posVelCompSrc = SvgToSource(sessionCache.PositionVelocityComparison);
            Dispatcher.UIThread.Post(() => { MiscPage.PositionVelocityComparison = SourceToImage(posVelCompSrc); });
        }));

        if (telemetryData.Front.Present)
        {
            tasks.Add(ThrottledPlotTask(() =>
            {
                var fpv = new PositionVelocityPlot(new Plot(), SuspensionType.Front);
                fpv.LoadTelemetryData(telemetryData);
                sessionCache.FrontPositionVelocity = fpv.Plot.GetSvgXml(width, height);
                var frontPosVelSrc = SvgToSource(sessionCache.FrontPositionVelocity);
                Dispatcher.UIThread.Post(() => { MiscPage.FrontPositionVelocity = SourceToImage(frontPosVelSrc); });
            }));
        }

        if (telemetryData.Rear.Present)
        {
            tasks.Add(ThrottledPlotTask(() =>
            {
                var rpv = new PositionVelocityPlot(new Plot(), SuspensionType.Rear);
                rpv.LoadTelemetryData(telemetryData);
                sessionCache.RearPositionVelocity = rpv.Plot.GetSvgXml(width, height);
                var rearPosVelSrc = SvgToSource(sessionCache.RearPositionVelocity);
                Dispatcher.UIThread.Post(() => { MiscPage.RearPositionVelocity = SourceToImage(rearPosVelSrc); });
            }));
        }

        // Summary runs concurrently with all plots (reuses shared VelocityBands tasks)
        tasks.Add(Task.Run(async () =>
        {
            var summaryData = await PopulateSummary(telemetryData, frontBandsTask, rearBandsTask);
            sessionCache.SummaryJson = JsonSerializer.Serialize(summaryData);
        }));

        await Task.WhenAll(tasks);

        await databaseService.PutSessionCacheAsync(sessionCache);
    }

    private sealed record SuspensionSummaryStats(
        double MaxTravel,
        double AvgTravel,
        double P95Travel,
        int Bottomouts,
        double AvgCompression,
        double MaxCompression,
        double Comp95th,
        double AvgRebound,
        double MaxRebound,
        double Reb95th);

    private sealed record CachedSummaryData(
        string[][] RunDataRows,
        string[][] ForkShockRows,
        string[][] WheelRows);

    private static string FormatTravel(double value, double maxTravel)
    {
        if (maxTravel <= 0)
        {
            return "-";
        }

        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{value / maxTravel * 100.0:0.0} % - {value:0.0} mm");
    }

    private static string FormatVelocity(double value)
    {
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{value:0.0} mm/s");
    }

    private static string FormatPercent(double value)
    {
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{value:0.0}");
    }

    private static string FormatBottomouts(int value) => $"{value} times";

    private static double EvaluatePolynomial(IReadOnlyList<double> coefficients, double x)
    {
        var result = 0.0;
        var power = 1.0;
        for (var i = 0; i < coefficients.Count; i++)
        {
            result += coefficients[i] * power;
            power *= x;
        }
        return result;
    }

    private static double EvaluateDerivative(IReadOnlyList<double> coefficients, double x)
    {
        var result = 0.0;
        var power = 1.0;
        for (var i = 1; i < coefficients.Count; i++)
        {
            result += i * coefficients[i] * power;
            power *= x;
        }
        return result;
    }

    private static double SolveShockTravel(double wheelTravel, IReadOnlyList<double> coefficients, double maxShockStroke)
    {
        if (wheelTravel <= 0)
        {
            return 0.0;
        }

        var maxWheelTravel = EvaluatePolynomial(coefficients, maxShockStroke);
        var x = maxWheelTravel > 0 ? wheelTravel / maxWheelTravel * maxShockStroke : 0.0;
        x = Math.Clamp(x, 0.0, maxShockStroke);

        for (var i = 0; i < 12; i++)
        {
            var f = EvaluatePolynomial(coefficients, x) - wheelTravel;
            if (Math.Abs(f) < 1e-6)
            {
                break;
            }

            var df = EvaluateDerivative(coefficients, x);
            if (Math.Abs(df) < 1e-6)
            {
                break;
            }

            x = Math.Clamp(x - f / df, 0.0, maxShockStroke);
        }

        return x;
    }

    private static SuspensionSummaryStats? BuildWheelStats(TelemetryData telemetryData, SuspensionType type)
    {
        var suspension = type == SuspensionType.Front ? telemetryData.Front : telemetryData.Rear;
        if (!suspension.Present)
        {
            return null;
        }

        var travelStats = telemetryData.CalculateTravelStatistics(type);
        var detailedTravel = telemetryData.CalculateDetailedTravelStatistics(type);
        var velocityStats = telemetryData.CalculateVelocityStatistics(type);

        var compressionVels = suspension.Strokes.Compressions
            .SelectMany(s => suspension.Velocity[s.Start..(s.End + 1)])
            .ToList();
        var reboundVels = suspension.Strokes.Rebounds
            .SelectMany(s => suspension.Velocity[s.Start..(s.End + 1)].Select(Math.Abs))
            .ToList();

        return new SuspensionSummaryStats(
            travelStats.Max,
            travelStats.Average,
            detailedTravel.P95,
            travelStats.Bottomouts,
            velocityStats.AverageCompression,
            velocityStats.MaxCompression,
            compressionVels.Count > 0 ? compressionVels.Percentile(95) : 0.0,
            velocityStats.AverageRebound,
            velocityStats.MaxRebound,
            reboundVels.Count > 0 ? -reboundVels.Percentile(95) : 0.0);
    }

    private static SuspensionSummaryStats? BuildForkStats(TelemetryData telemetryData)
    {
        if (!telemetryData.Front.Present || !telemetryData.Linkage.MaxFrontStroke.HasValue || telemetryData.Linkage.MaxFrontStroke <= 0)
        {
            return null;
        }

        var maxForkStroke = telemetryData.Linkage.MaxFrontStroke.Value;
        var frontCoeff = Math.Sin(telemetryData.Linkage.HeadAngle * Math.PI / 180.0);
        if (frontCoeff < 1e-6) return null;
        var invCoeff = 1.0 / frontCoeff;

        var wheelTravel = telemetryData.Front.Travel;
        var wheelVelocity = telemetryData.Front.Velocity;
        var forkTravel = new double[wheelTravel.Length];
        var forkVelocity = new double[wheelVelocity.Length];

        for (var i = 0; i < wheelTravel.Length; i++)
        {
            forkTravel[i] = Math.Min(wheelTravel[i] * invCoeff, maxForkStroke);
        }

        for (var i = 0; i < wheelVelocity.Length; i++)
        {
            forkVelocity[i] = wheelVelocity[i] * invCoeff;
        }

        var compSamples = telemetryData.Front.Strokes.Compressions.Sum(s => s.Stat.Count);
        var rebSamples = telemetryData.Front.Strokes.Rebounds.Sum(s => s.Stat.Count);
        var totalSamples = compSamples + rebSamples;

        double travelSum = 0.0;
        var travelCount = 0;
        double travelMax = 0.0;
        var travelValues = new List<double>(totalSamples);
        double compressionSum = 0.0;
        var compressionCount = 0;
        double compressionMax = 0.0;
        var compressionVels = new List<double>(compSamples);
        double reboundSum = 0.0;
        var reboundCount = 0;
        double reboundMax = 0.0;
        var reboundVels = new List<double>(rebSamples);

        foreach (var stroke in telemetryData.Front.Strokes.Compressions.Concat(telemetryData.Front.Strokes.Rebounds))
        {
            for (var i = stroke.Start; i <= stroke.End && i < forkTravel.Length; i++)
            {
                var t = forkTravel[i];
                travelValues.Add(t);
                travelSum += t;
                travelCount++;
                if (t > travelMax) travelMax = t;
            }
        }

        foreach (var stroke in telemetryData.Front.Strokes.Compressions)
        {
            for (var i = stroke.Start; i <= stroke.End && i < forkVelocity.Length; i++)
            {
                var v = forkVelocity[i];
                compressionSum += v;
                compressionCount++;
                compressionVels.Add(v);
                if (v > compressionMax) compressionMax = v;
            }
        }

        foreach (var stroke in telemetryData.Front.Strokes.Rebounds)
        {
            for (var i = stroke.Start; i <= stroke.End && i < forkVelocity.Length; i++)
            {
                var v = forkVelocity[i];
                reboundSum += v;
                reboundCount++;
                reboundVels.Add(Math.Abs(v));
                if (v < reboundMax) reboundMax = v;
            }
        }

        var bottomouts = 0;
        var threshold = maxForkStroke * 0.97;
        for (var i = 0; i < forkTravel.Length; i++)
        {
            if (forkTravel[i] <= threshold) continue;
            bottomouts++;
            while (i < forkTravel.Length && forkTravel[i] > threshold) i++;
        }

        if (travelCount == 0) return null;

        return new SuspensionSummaryStats(
            travelMax,
            travelSum / travelCount,
            travelValues.Count > 0 ? travelValues.Percentile(95) : 0.0,
            bottomouts,
            compressionCount > 0 ? compressionSum / compressionCount : 0.0,
            compressionMax,
            compressionVels.Count > 0 ? compressionVels.Percentile(95) : 0.0,
            reboundCount > 0 ? reboundSum / reboundCount : 0.0,
            reboundMax,
            reboundVels.Count > 0 ? -reboundVels.Percentile(95) : 0.0);
    }

    private static SuspensionSummaryStats? BuildShockStats(TelemetryData telemetryData)
    {
        if (!telemetryData.Rear.Present || !telemetryData.Linkage.MaxRearStroke.HasValue || telemetryData.Linkage.MaxRearStroke <= 0)
        {
            return null;
        }

        var maxShockStroke = telemetryData.Linkage.MaxRearStroke.Value;
        var coeffs = telemetryData.Linkage.ShockWheelCoeffs;
        var rearTravel = telemetryData.Rear.Travel;
        var rearVelocity = telemetryData.Rear.Velocity;
        var shockTravel = new double[rearTravel.Length];
        var shockVelocity = new double[rearVelocity.Length];

        Parallel.For(0, rearTravel.Length, i =>
        {
            var s = SolveShockTravel(rearTravel[i], coeffs, maxShockStroke);
            shockTravel[i] = s;
            var derivative = EvaluateDerivative(coeffs, s);
            shockVelocity[i] = Math.Abs(derivative) > 1e-6 ? rearVelocity[i] / derivative : 0.0;
        });

        var compSamples = telemetryData.Rear.Strokes.Compressions.Sum(s => s.Stat.Count);
        var rebSamples = telemetryData.Rear.Strokes.Rebounds.Sum(s => s.Stat.Count);
        var totalSamples = compSamples + rebSamples;

        double travelSum = 0.0;
        var travelCount = 0;
        double travelMax = 0.0;
        var travelValues = new List<double>(totalSamples);
        double compressionSum = 0.0;
        var compressionCount = 0;
        double compressionMax = 0.0;
        var compressionVels = new List<double>(compSamples);
        double reboundSum = 0.0;
        var reboundCount = 0;
        double reboundMax = 0.0;
        var reboundVels = new List<double>(rebSamples);

        foreach (var stroke in telemetryData.Rear.Strokes.Compressions.Concat(telemetryData.Rear.Strokes.Rebounds))
        {
            for (var i = stroke.Start; i <= stroke.End && i < shockTravel.Length; i++)
            {
                var t = shockTravel[i];
                travelValues.Add(t);
                travelSum += t;
                travelCount++;
                if (t > travelMax)
                {
                    travelMax = t;
                }
            }
        }

        foreach (var stroke in telemetryData.Rear.Strokes.Compressions)
        {
            for (var i = stroke.Start; i <= stroke.End && i < shockVelocity.Length; i++)
            {
                var v = shockVelocity[i];
                compressionSum += v;
                compressionCount++;
                compressionVels.Add(v);
                if (v > compressionMax)
                {
                    compressionMax = v;
                }
            }
        }

        foreach (var stroke in telemetryData.Rear.Strokes.Rebounds)
        {
            for (var i = stroke.Start; i <= stroke.End && i < shockVelocity.Length; i++)
            {
                var v = shockVelocity[i];
                reboundSum += v;
                reboundCount++;
                reboundVels.Add(Math.Abs(v));
                if (v < reboundMax)
                {
                    reboundMax = v;
                }
            }
        }

        var bottomouts = 0;
        var threshold = maxShockStroke * 0.97;
        for (var i = 0; i < shockTravel.Length; i++)
        {
            if (shockTravel[i] <= threshold)
            {
                continue;
            }

            bottomouts++;
            while (i < shockTravel.Length && shockTravel[i] > threshold)
            {
                i++;
            }
        }

        if (travelCount == 0)
        {
            return null;
        }

        return new SuspensionSummaryStats(
            travelMax,
            travelSum / travelCount,
            travelValues.Count > 0 ? travelValues.Percentile(95) : 0.0,
            bottomouts,
            compressionCount > 0 ? compressionSum / compressionCount : 0.0,
            compressionMax,
            compressionVels.Count > 0 ? compressionVels.Percentile(95) : 0.0,
            reboundCount > 0 ? reboundSum / reboundCount : 0.0,
            reboundMax,
            reboundVels.Count > 0 ? -reboundVels.Percentile(95) : 0.0);
    }

    private Task<CachedSummaryData> PopulateSummary(TelemetryData telemetryData) =>
        PopulateSummary(telemetryData,
            Task.FromResult(telemetryData.Front.Present ? (VelocityBands?)telemetryData.CalculateVelocityBands(SuspensionType.Front, 200) : null),
            Task.FromResult(telemetryData.Rear.Present ? (VelocityBands?)telemetryData.CalculateVelocityBands(SuspensionType.Rear, 200) : null));

    private async Task<CachedSummaryData> PopulateSummary(
        TelemetryData telemetryData,
        Task<VelocityBands?> frontBandsTask,
        Task<VelocityBands?> rearBandsTask)
    {
        var date = (Timestamp ?? DateTime.UnixEpoch).ToString("yyyy-MM-dd");
        var time = (Timestamp ?? DateTime.UnixEpoch).ToString("HH:mm");
        var sampleCount = Math.Max(telemetryData.Front.Travel?.Length ?? 0, telemetryData.Rear.Travel?.Length ?? 0);
        var duration = telemetryData.SampleRate > 0
            ? TimeSpan.FromSeconds(sampleCount / (double)telemetryData.SampleRate)
            : TimeSpan.Zero;
        var runDuration = duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss")
            : duration.ToString(@"m\:ss");

        var databaseService = App.Current?.Services?.GetService<IDatabaseService>();
        var setupName = "-";
        var frontCalName = "-";
        var rearCalName = "-";
        if (databaseService != null && session.Setup.HasValue)
        {
            var setup = await databaseService.GetSetupAsync(session.Setup.Value);
            if (setup != null)
            {
                setupName = setup.Name;
                if (setup.FrontCalibrationId.HasValue)
                    frontCalName = (await databaseService.GetCalibrationAsync(setup.FrontCalibrationId.Value))?.Name ?? "-";
                if (setup.RearCalibrationId.HasValue)
                    rearCalName = (await databaseService.GetCalibrationAsync(setup.RearCalibrationId.Value))?.Name ?? "-";
            }
        }
        var linkageName = telemetryData.Linkage?.Name ?? "-";

        // Run the independent (read-only) computations in parallel
        var forkStatsTask = Task.Run(() => BuildForkStats(telemetryData));
        var frontWheelStatsTask = Task.Run(() => BuildWheelStats(telemetryData, SuspensionType.Front));
        var shockStatsTask = Task.Run(() => BuildShockStats(telemetryData));
        var rearWheelStatsTask = Task.Run(() => BuildWheelStats(telemetryData, SuspensionType.Rear));
        await Task.WhenAll(forkStatsTask, frontWheelStatsTask, shockStatsTask, rearWheelStatsTask, frontBandsTask, rearBandsTask);

        var forkStats = forkStatsTask.Result;
        var shockStats = shockStatsTask.Result;
        var frontWheelStats = frontWheelStatsTask.Result;
        var rearWheelStats = rearWheelStatsTask.Result;
        var frontBands = frontBandsTask.Result;
        var rearBands = rearBandsTask.Result;

        var runDataRows = new ObservableCollection<SummaryValueRow>(
        [
            new SummaryValueRow("Date", date),
            new SummaryValueRow("Time", time),
            new SummaryValueRow("Run duration", runDuration),
            new SummaryValueRow("Front cal.", frontCalName),
            new SummaryValueRow("Rear cal.", rearCalName),
            new SummaryValueRow("Linkage", linkageName),
        ]);

        var forkShockRows = new ObservableCollection<SummaryComparisonRow>(
        [
            new SummaryComparisonRow("Pos [AVG]",
                forkStats is null ? "-" : FormatTravel(forkStats.AvgTravel, telemetryData.Linkage.MaxFrontStroke ?? 0),
                shockStats is null ? "-" : FormatTravel(shockStats.AvgTravel, telemetryData.Linkage.MaxRearStroke ?? 0)),
            new SummaryComparisonRow("Pos [95th]",
                forkStats is null ? "-" : FormatTravel(forkStats.P95Travel, telemetryData.Linkage.MaxFrontStroke ?? 0),
                shockStats is null ? "-" : FormatTravel(shockStats.P95Travel, telemetryData.Linkage.MaxRearStroke ?? 0)),
            new SummaryComparisonRow("Pos [MAX]",
                forkStats is null ? "-" : FormatTravel(forkStats.MaxTravel, telemetryData.Linkage.MaxFrontStroke ?? 0),
                shockStats is null ? "-" : FormatTravel(shockStats.MaxTravel, telemetryData.Linkage.MaxRearStroke ?? 0)),
            new SummaryComparisonRow("Bottom out",
                forkStats is null ? "-" : FormatBottomouts(forkStats.Bottomouts),
                shockStats is null ? "-" : FormatBottomouts(shockStats.Bottomouts)),
            new SummaryComparisonRow("Comp [AVG]",
                forkStats is null ? "-" : FormatVelocity(forkStats.AvgCompression),
                shockStats is null ? "-" : FormatVelocity(shockStats.AvgCompression)),
            new SummaryComparisonRow("Reb [AVG]",
                forkStats is null ? "-" : FormatVelocity(forkStats.AvgRebound),
                shockStats is null ? "-" : FormatVelocity(shockStats.AvgRebound)),
            new SummaryComparisonRow("Comp [95th]",
                forkStats is null ? "-" : FormatVelocity(forkStats.Comp95th),
                shockStats is null ? "-" : FormatVelocity(shockStats.Comp95th)),
            new SummaryComparisonRow("Reb [95th]",
                forkStats is null ? "-" : FormatVelocity(forkStats.Reb95th),
                shockStats is null ? "-" : FormatVelocity(shockStats.Reb95th)),
            new SummaryComparisonRow("Comp [MAX]",
                forkStats is null ? "-" : FormatVelocity(forkStats.MaxCompression),
                shockStats is null ? "-" : FormatVelocity(shockStats.MaxCompression)),
            new SummaryComparisonRow("Reb [MAX]",
                forkStats is null ? "-" : FormatVelocity(forkStats.MaxRebound),
                shockStats is null ? "-" : FormatVelocity(shockStats.MaxRebound))
        ]);

        var wheelRows = new ObservableCollection<SummaryComparisonRow>(
        [
            new SummaryComparisonRow("Pos [AVG]",
                frontWheelStats is null ? "-" : FormatTravel(frontWheelStats.AvgTravel, telemetryData.Linkage.MaxFrontTravel),
                rearWheelStats is null ? "-" : FormatTravel(rearWheelStats.AvgTravel, telemetryData.Linkage.MaxRearTravel)),
            new SummaryComparisonRow("Pos [95th]",
                frontWheelStats is null ? "-" : FormatTravel(frontWheelStats.P95Travel, telemetryData.Linkage.MaxFrontTravel),
                rearWheelStats is null ? "-" : FormatTravel(rearWheelStats.P95Travel, telemetryData.Linkage.MaxRearTravel)),
            new SummaryComparisonRow("Pos [MAX]",
                frontWheelStats is null ? "-" : FormatTravel(frontWheelStats.MaxTravel, telemetryData.Linkage.MaxFrontTravel),
                rearWheelStats is null ? "-" : FormatTravel(rearWheelStats.MaxTravel, telemetryData.Linkage.MaxRearTravel)),
            new SummaryComparisonRow("Bottom out",
                frontWheelStats is null ? "-" : FormatBottomouts(frontWheelStats.Bottomouts),
                rearWheelStats is null ? "-" : FormatBottomouts(rearWheelStats.Bottomouts)),
            new SummaryComparisonRow("Comp [AVG]",
                frontWheelStats is null ? "-" : FormatVelocity(frontWheelStats.AvgCompression),
                rearWheelStats is null ? "-" : FormatVelocity(rearWheelStats.AvgCompression)),
            new SummaryComparisonRow("Reb [AVG]",
                frontWheelStats is null ? "-" : FormatVelocity(frontWheelStats.AvgRebound),
                rearWheelStats is null ? "-" : FormatVelocity(rearWheelStats.AvgRebound)),
            new SummaryComparisonRow("Comp [95th]",
                frontWheelStats is null ? "-" : FormatVelocity(frontWheelStats.Comp95th),
                rearWheelStats is null ? "-" : FormatVelocity(rearWheelStats.Comp95th)),
            new SummaryComparisonRow("Reb [95th]",
                frontWheelStats is null ? "-" : FormatVelocity(frontWheelStats.Reb95th),
                rearWheelStats is null ? "-" : FormatVelocity(rearWheelStats.Reb95th)),
            new SummaryComparisonRow("Comp [MAX]",
                frontWheelStats is null ? "-" : FormatVelocity(frontWheelStats.MaxCompression),
                rearWheelStats is null ? "-" : FormatVelocity(rearWheelStats.MaxCompression)),
            new SummaryComparisonRow("Reb [MAX]",
                frontWheelStats is null ? "-" : FormatVelocity(frontWheelStats.MaxRebound),
                rearWheelStats is null ? "-" : FormatVelocity(rearWheelStats.MaxRebound)),
            new SummaryComparisonRow("HSR [%]",
                frontBands is null ? "-" : FormatPercent(frontBands.HighSpeedRebound),
                rearBands is null ? "-" : FormatPercent(rearBands.HighSpeedRebound)),
            new SummaryComparisonRow("LSR [%]",
                frontBands is null ? "-" : FormatPercent(frontBands.LowSpeedRebound),
                rearBands is null ? "-" : FormatPercent(rearBands.LowSpeedRebound)),
            new SummaryComparisonRow("LSC [%]",
                frontBands is null ? "-" : FormatPercent(frontBands.LowSpeedCompression),
                rearBands is null ? "-" : FormatPercent(rearBands.LowSpeedCompression)),
            new SummaryComparisonRow("HSC [%]",
                frontBands is null ? "-" : FormatPercent(frontBands.HighSpeedCompression),
                rearBands is null ? "-" : FormatPercent(rearBands.HighSpeedCompression))
        ]);

        Dispatcher.UIThread.Post(() =>
        {
            SummaryPage.RunDataRows = runDataRows;
            SummaryPage.ForkShockRows = forkShockRows;
            SummaryPage.WheelRows = wheelRows;
        });

        return new CachedSummaryData(
            runDataRows.Select(r => new[] { r.Label, r.Value }).ToArray(),
            forkShockRows.Select(r => new[] { r.Label, r.LeftValue, r.RightValue }).ToArray(),
            wheelRows.Select(r => new[] { r.Label, r.LeftValue, r.RightValue }).ToArray());
    }

    #endregion

    #region Constructors

    public SessionViewModel()
    {
        session = new Session();
        IsInDatabase = false;
        Pages = [SummaryPage, SpringPage, DamperPage, BalancePage, MiscPage, NotesPage];
        SummaryPage.ChangeSetupCommand = new AsyncRelayCommand(HandleSetupReassign);
    }

    public SessionViewModel(Session session, bool fromDatabase)
    {
        this.session = session;
        IsInDatabase = fromDatabase;
        Pages = [SummaryPage, SpringPage, DamperPage, BalancePage, MiscPage, NotesPage];
        SummaryPage.ChangeSetupCommand = new AsyncRelayCommand(HandleSetupReassign);

        NotesPage.ForkSettings.PropertyChanged += (_, _) => EvaluateDirtiness();
        NotesPage.ShockSettings.PropertyChanged += (_, _) => EvaluateDirtiness();
        NotesPage.PropertyChanged += (_, _) => EvaluateDirtiness();

        ResetImplementation();
    }

    #endregion

    #region ItemViewModelBase overrides
    protected override void EvaluateDirtiness()
    {
        IsDirty =
            !IsInDatabase ||
            Name != session.Name ||
            NotesPage.IsDirty(session);
    }

    protected override async Task SaveImplementation()
    {
        var databaseService = App.Current?.Services?.GetService<IDatabaseService>();
        Debug.Assert(databaseService != null, nameof(databaseService) + " != null");

        try
        {
            var newSession = new Session(
                id: session.Id,
                name: Name ?? $"session #{session.Id}",
                description: NotesPage.Description ?? $"session #{session.Id}",
                setup: session.Setup)
            {
                FrontSpringRate = NotesPage.ForkSettings.SpringRate,
                FrontVolSpc = NotesPage.ForkSettings.VolSpc,
                FrontHighSpeedCompression = NotesPage.ForkSettings.HighSpeedCompression,
                FrontLowSpeedCompression = NotesPage.ForkSettings.LowSpeedCompression,
                FrontLowSpeedRebound = NotesPage.ForkSettings.LowSpeedRebound,
                FrontHighSpeedRebound = NotesPage.ForkSettings.HighSpeedRebound,
                RearSpringRate = NotesPage.ShockSettings.SpringRate,
                RearVolSpc = NotesPage.ShockSettings.VolSpc,
                RearHighSpeedCompression = NotesPage.ShockSettings.HighSpeedCompression,
                RearLowSpeedCompression = NotesPage.ShockSettings.LowSpeedCompression,
                RearLowSpeedRebound = NotesPage.ShockSettings.LowSpeedRebound,
                RearHighSpeedRebound = NotesPage.ShockSettings.HighSpeedRebound,
                HasProcessedData = IsComplete,
            };

            await databaseService.PutSessionAsync(newSession);
            session = newSession;
            IsDirty = false;
            IsInDatabase = true;
        }
        catch (Exception e)
        {
            ErrorMessages.Add($"Session could not be saved: {e.Message}");
        }
    }

    protected override Task ResetImplementation()
    {
        Id = session.Id;
        Name = session.Name;

        NotesPage.Description = session.Description;
        NotesPage.ForkSettings.SpringRate = session.FrontSpringRate;
        NotesPage.ForkSettings.VolSpc = session.FrontVolSpc;
        NotesPage.ForkSettings.HighSpeedCompression = session.FrontHighSpeedCompression;
        NotesPage.ForkSettings.LowSpeedCompression = session.FrontLowSpeedCompression;
        NotesPage.ForkSettings.LowSpeedRebound = session.FrontLowSpeedRebound;
        NotesPage.ForkSettings.HighSpeedRebound = session.FrontHighSpeedRebound;

        NotesPage.ShockSettings.SpringRate = session.RearSpringRate;
        NotesPage.ShockSettings.VolSpc = session.RearVolSpc;
        NotesPage.ShockSettings.HighSpeedCompression = session.RearHighSpeedCompression;
        NotesPage.ShockSettings.LowSpeedCompression = session.RearLowSpeedCompression;
        NotesPage.ShockSettings.LowSpeedRebound = session.RearLowSpeedRebound;
        NotesPage.ShockSettings.HighSpeedRebound = session.RearHighSpeedRebound;

        Timestamp = DateTimeOffset.FromUnixTimeSeconds(session.Timestamp ?? 0).DateTime;

        return Task.CompletedTask;
    }

    #endregion

    #region Commands

    // Called after import to pre-generate the plot cache in the background,
    // before the user opens the session. Uses the last known bounds (updated on each Loaded call).
    internal async Task PrecomputeCache()
    {
        try
        {
            if (!IsComplete) return;
            var databaseService = App.Current?.Services?.GetService<IDatabaseService>();
            if (databaseService is null) return;

            var cacheExists = await databaseService.GetSessionCacheAsync(Id) is not null;
            if (cacheExists) return;

            var telemetryData = await databaseService.GetSessionPsstAsync(Id);
            if (telemetryData is null) return;

            await CreateCache(LastKnownBounds, telemetryData);
        }
        catch
        {
            // Best-effort — user opening the session will retry
        }
    }

    private async Task HandleSetupReassign()
    {
        var newSetup = SummaryPage.SelectedSetup;
        if (newSetup == null || newSetup.Id == session.Setup) return;

        var databaseService = App.Current?.Services?.GetService<IDatabaseService>();
        Debug.Assert(databaseService != null, nameof(databaseService) + " != null");

        try
        {
            IsAnalyzingData = true;
            if (IsCombinedSession)
            {
                var idsToReassign = new HashSet<Guid> { Id };
                var visitedCombined = new HashSet<Guid>();

                async Task CollectLeafSourceSessionIds(Guid combinedId)
                {
                    if (!visitedCombined.Add(combinedId))
                        return;

                    var sourceIds = await databaseService.GetCombinedSourcesAsync(combinedId);
                    foreach (var sourceId in sourceIds)
                    {
                        var nestedSources = await databaseService.GetCombinedSourcesAsync(sourceId);
                        if (nestedSources.Count == 0)
                        {
                            idsToReassign.Add(sourceId);
                        }
                        else
                        {
                            await CollectLeafSourceSessionIds(sourceId);
                        }
                    }
                }

                await CollectLeafSourceSessionIds(Id);
                foreach (var sessionId in idsToReassign)
                    await databaseService.ReassignSessionSetupAsync(sessionId, newSetup.Id);
            }
            else
            {
                await databaseService.ReassignSessionSetupAsync(Id, newSetup.Id);
            }

            session.Setup = newSetup.Id;
            foreach (var subSession in SubSessions)
                subSession.SessionModel.Setup = newSetup.Id;
            var telemetryData = await databaseService.GetSessionPsstAsync(Id);
            if (telemetryData != null)
                await CreateCache(LastKnownBounds, telemetryData);
        }
        catch (Exception e)
        {
            ErrorMessages.Add($"Setup reassignment failed: {e.Message}");
        }
        finally
        {
            IsAnalyzingData = false;
        }
    }

    [RelayCommand]
    private async Task Loaded(Rect bounds)
    {
        try
        {
            LastKnownBounds = bounds;
            var databaseService = App.Current?.Services?.GetService<IDatabaseService>();
            Debug.Assert(databaseService != null, nameof(databaseService) + " != null");

            var mainPagesViewModel = App.Current?.Services?.GetService<MainPagesViewModel>();
            if (mainPagesViewModel != null)
            {
                var allSetups = mainPagesViewModel.SetupsPage.Items.OfType<SetupViewModel>().ToList();
                SummaryPage.AvailableSetups.Clear();
                foreach (var s in allSetups) SummaryPage.AvailableSetups.Add(s);
                SummaryPage.SelectedSetup = allSetups.FirstOrDefault(s => s.Id == session.Setup);
            }

            if (!IsComplete)
            {
                var httpApiService = App.Current?.Services?.GetService<IHttpApiService>();
                Debug.Assert(httpApiService != null, nameof(httpApiService) + " != null");

                var psst = await httpApiService.GetSessionPsstAsync(Id) ?? throw new Exception("Session data could not be downloaded from server.");
                await databaseService.PatchSessionPsstAsync(Id, psst);
                session.HasProcessedData = true;
            }

            var (cacheLoaded, hasVdc, hasPvc) = await LoadCache();

            // Use cache-row flags (hasVdc/hasPvc) instead of in-memory properties —
            // the background lazy-load task hasn't set DamperPage/MiscPage properties yet.
            var needsRecreate = !cacheLoaded ||
                ((SpringPage.FrontTravelHistogram is not null || SpringPage.RearTravelHistogram is not null) && !hasVdc) ||
                (SpringPage.TravelComparisonHistogram is not null && SpringPage.FrontRearTravelScatter is null) ||
                !hasPvc;

            var needsSummary = SummaryPage.RunDataRows.Count == 0;

            // Only hit the DB if we actually need to rebuild cache or summary
            if (needsRecreate || needsSummary)
            {
                var telemetryData = await databaseService.GetSessionPsstAsync(Id);

                if (needsRecreate)
                {
                    if (telemetryData is null)
                    {
                        throw new Exception("Database error");
                    }

                    // CreateCache also populates summary and persists both
                    IsAnalyzingData = true;
                    try { await CreateCache(bounds, telemetryData); }
                    finally { IsAnalyzingData = false; }
                }
                else if (telemetryData is not null && needsSummary)
                {
                    // Cache was valid but summary was missing (old cache without summary_json)
                    var summaryData = await PopulateSummary(telemetryData);

                    var cache = await databaseService.GetSessionCacheAsync(Id);
                    if (cache is not null)
                    {
                        cache.SummaryJson = JsonSerializer.Serialize(summaryData);
                        await databaseService.PutSessionCacheAsync(cache);
                    }
                }
            }
        }
        catch (Exception e)
        {
            ErrorMessages.Add($"Could not load session data: {e.Message}");
        }
    }

    protected override bool CanExportPdf() => IsComplete;

    protected override async Task ExportPdf()
    {
        App.Current?.Services?.GetService<IHapticFeedback>()?.Click();
        IsGeneratingPdf = true;
        try
        {
            var databaseService = App.Current?.Services?.GetService<IDatabaseService>();
            Debug.Assert(databaseService != null, nameof(databaseService) + " != null");

            var cache = await databaseService.GetSessionCacheAsync(Id);
            if (cache is null)
            {
                ErrorMessages.Add("No cached plots found. Open the session first.");
                return;
            }

            // Collect all SVG strings in tab display order
            var svgEntries = new List<string?>();

            // Spring tab
            svgEntries.Add(cache.TravelComparisonHistogram);
            svgEntries.Add(cache.FrontRearTravelScatter);
            svgEntries.Add(cache.FrontTravelHistogram);
            svgEntries.Add(cache.RearTravelHistogram);

            // Damper tab
            svgEntries.Add(cache.VelocityDistributionComparison);
            svgEntries.Add(cache.FrontVelocityHistogram);
            svgEntries.Add(cache.FrontLowSpeedVelocityHistogram);
            svgEntries.Add(cache.RearVelocityHistogram);
            svgEntries.Add(cache.RearLowSpeedVelocityHistogram);

            // Balance tab
            svgEntries.Add(cache.CombinedBalance);
            svgEntries.Add(cache.CompressionBalance);
            svgEntries.Add(cache.ReboundBalance);

            // Misc tab
            svgEntries.Add(cache.PositionVelocityComparison);
            svgEntries.Add(cache.FrontPositionVelocity);
            svgEntries.Add(cache.RearPositionVelocity);

            var validSvgs = svgEntries.Where(s => s is not null).Cast<string>().ToList();
            if (validSvgs.Count == 0)
            {
                ErrorMessages.Add("No plots to export.");
                return;
            }

            var pdfPath = await Task.Run(() => RenderSvgsToPdf(validSvgs, SummaryPage, NotesPage));

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

    private static void DrawSummaryPage(SkiaSharp.SKDocument document, SummaryPageViewModel summary, float pageWidth)
    {
        const float margin = 30f;
        const float rowH = 26f;
        const float titleH = 28f;
        const float sectionGap = 18f;
        const float fontSize = 11f;
        const float titleFontSize = 10f;

        float contentWidth = pageWidth - margin * 2f;
        float col0 = 95f;
        float col12 = (contentWidth - col0) / 2f;

        var bgColor       = SkiaSharp.SKColor.Parse("#15191c");
        var cellBg        = SkiaSharp.SKColor.Parse("#20262b");
        var headerBg      = SkiaSharp.SKColor.Parse("#66c2a5");
        var headerFg      = SkiaSharp.SKColor.Parse("#15191c");
        var cellFg        = SkiaSharp.SKColor.Parse("#a0a0a0");
        var borderColor   = SkiaSharp.SKColor.Parse("#505050");

        // Calculate total page height
        float pageHeight = margin * 2f
            + titleH + summary.RunDataRows.Count * rowH
            + sectionGap
            + rowH + summary.WheelRows.Count * rowH
            + sectionGap
            + rowH + summary.ForkShockRows.Count * rowH;

        using var canvas = document.BeginPage(pageWidth, pageHeight);
        canvas.Clear(bgColor);

        using var fillPaint   = new SkiaSharp.SKPaint { IsStroke = false };
        using var strokePaint = new SkiaSharp.SKPaint { IsStroke = true, StrokeWidth = 0.75f, Color = borderColor };
        using var textPaint   = new SkiaSharp.SKPaint { IsAntialias = true, TextSize = fontSize };
        using var boldPaint   = new SkiaSharp.SKPaint { IsAntialias = true, TextSize = titleFontSize,
            Typeface = SkiaSharp.SKTypeface.FromFamilyName(null, SkiaSharp.SKFontStyle.Bold) };

        void DrawCell(float x, float y, float w, float h, SkiaSharp.SKColor bg, SkiaSharp.SKColor fg,
                      string text, bool rightAlign, bool bold)
        {
            var rect = new SkiaSharp.SKRect(x, y, x + w, y + h);
            fillPaint.Color = bg;
            canvas.DrawRect(rect, fillPaint);
            canvas.DrawRect(rect, strokePaint);
            var p = bold ? boldPaint : textPaint;
            p.Color = fg;
            float tw = p.MeasureText(text);
            float tx = rightAlign ? x + w - 6f - tw : x + 6f;
            float ty = y + h / 2f + p.TextSize * 0.35f;
            canvas.DrawText(text, tx, ty, p);
        }

        float curY = margin;

        // RUN DATA
        DrawCell(margin, curY, contentWidth, titleH, headerBg, headerFg, "RUN DATA", false, true);
        curY += titleH;
        foreach (var row in summary.RunDataRows)
        {
            DrawCell(margin,          curY, col0,            rowH, cellBg, cellFg, row.Label, false, false);
            DrawCell(margin + col0,   curY, contentWidth - col0, rowH, cellBg, cellFg, row.Value, true,  false);
            curY += rowH;
        }

        curY += sectionGap;

        // WHEEL
        DrawCell(margin,              curY, col0,  rowH, headerBg, headerFg, "",             false, true);
        DrawCell(margin + col0,       curY, col12, rowH, headerBg, headerFg, "FRONT WHEEL",  true,  true);
        DrawCell(margin + col0 + col12, curY, col12, rowH, headerBg, headerFg, "REAR WHEEL", true,  true);
        curY += rowH;
        foreach (var row in summary.WheelRows)
        {
            DrawCell(margin,                curY, col0,  rowH, cellBg, cellFg, row.Label,      false, false);
            DrawCell(margin + col0,         curY, col12, rowH, cellBg, cellFg, row.LeftValue,  true,  false);
            DrawCell(margin + col0 + col12, curY, col12, rowH, cellBg, cellFg, row.RightValue, true,  false);
            curY += rowH;
        }

        curY += sectionGap;

        // FORK / SHOCK
        DrawCell(margin,                curY, col0,  rowH, headerBg, headerFg, "",      false, true);
        DrawCell(margin + col0,         curY, col12, rowH, headerBg, headerFg, "FORK",  true,  true);
        DrawCell(margin + col0 + col12, curY, col12, rowH, headerBg, headerFg, "SHOCK", true,  true);
        curY += rowH;
        foreach (var row in summary.ForkShockRows)
        {
            DrawCell(margin,                curY, col0,  rowH, cellBg, cellFg, row.Label,      false, false);
            DrawCell(margin + col0,         curY, col12, rowH, cellBg, cellFg, row.LeftValue,  true,  false);
            DrawCell(margin + col0 + col12, curY, col12, rowH, cellBg, cellFg, row.RightValue, true,  false);
            curY += rowH;
        }

        document.EndPage();
    }

    private static void DrawNotesPage(SkiaSharp.SKDocument document, NotesPageViewModel notes, float pageWidth)
    {
        const float margin = 30f;
        const float rowH = 26f;
        const float titleH = 28f;
        const float sectionGap = 18f;
        const float fontSize = 11f;
        const float titleFontSize = 10f;
        const float noteFontSize = 11f;

        float contentWidth = pageWidth - margin * 2f;
        float col0 = 95f;
        float col12 = (contentWidth - col0) / 2f;

        var bgColor     = SkiaSharp.SKColor.Parse("#15191c");
        var cellBg      = SkiaSharp.SKColor.Parse("#20262b");
        var headerBg    = SkiaSharp.SKColor.Parse("#66c2a5");
        var headerFg    = SkiaSharp.SKColor.Parse("#15191c");
        var cellFg      = SkiaSharp.SKColor.Parse("#a0a0a0");
        var borderColor = SkiaSharp.SKColor.Parse("#505050");

        var settingRows = new[]
        {
            ("Spring",  notes.ForkSettings.SpringRate,              notes.ShockSettings.SpringRate),
            ("VolSpc",  notes.ForkSettings.VolSpc?.ToString("F2"),  notes.ShockSettings.VolSpc?.ToString("F2")),
            ("HSC",     notes.ForkSettings.HighSpeedCompression?.ToString(), notes.ShockSettings.HighSpeedCompression?.ToString()),
            ("LSC",     notes.ForkSettings.LowSpeedCompression?.ToString(),  notes.ShockSettings.LowSpeedCompression?.ToString()),
            ("LSR",     notes.ForkSettings.LowSpeedRebound?.ToString(),      notes.ShockSettings.LowSpeedRebound?.ToString()),
            ("HSR",     notes.ForkSettings.HighSpeedRebound?.ToString(),     notes.ShockSettings.HighSpeedRebound?.ToString()),
        };

        bool hasDescription = !string.IsNullOrWhiteSpace(notes.Description);
        float noteHeight = 0f;
        string[] noteLines = [];
        using var notePaint = new SkiaSharp.SKPaint { IsAntialias = true, TextSize = noteFontSize };

        if (hasDescription)
        {
            // Word-wrap the description to fit contentWidth with 8px padding on each side
            float wrapWidth = contentWidth - 16f;
            var words = notes.Description!.Replace("\r\n", "\n").Replace("\r", "\n").Split(' ');
            var lines = new System.Collections.Generic.List<string>();
            var currentLine = "";
            foreach (var word in words)
            {
                foreach (var segment in word.Split('\n'))
                {
                    var test = currentLine.Length == 0 ? segment : currentLine + " " + segment;
                    if (notePaint.MeasureText(test) > wrapWidth && currentLine.Length > 0)
                    {
                        lines.Add(currentLine);
                        currentLine = segment;
                    }
                    else
                    {
                        currentLine = test;
                    }
                    if (word.Contains('\n') && segment != words[^1].Split('\n')[^1])
                    {
                        lines.Add(currentLine);
                        currentLine = "";
                    }
                }
            }
            if (currentLine.Length > 0) lines.Add(currentLine);
            noteLines = lines.ToArray();
            noteHeight = titleH + noteLines.Length * (noteFontSize + 4f) + 16f;
        }

        float pageHeight = margin * 2f
            + titleH + settingRows.Length * rowH
            + (hasDescription ? sectionGap + noteHeight : 0f);

        using var canvas = document.BeginPage(pageWidth, pageHeight);
        canvas.Clear(bgColor);

        using var fillPaint   = new SkiaSharp.SKPaint { IsStroke = false };
        using var strokePaint = new SkiaSharp.SKPaint { IsStroke = true, StrokeWidth = 0.75f, Color = borderColor };
        using var textPaint   = new SkiaSharp.SKPaint { IsAntialias = true, TextSize = fontSize };
        using var boldPaint   = new SkiaSharp.SKPaint { IsAntialias = true, TextSize = titleFontSize,
            Typeface = SkiaSharp.SKTypeface.FromFamilyName(null, SkiaSharp.SKFontStyle.Bold) };

        void DrawCell(float x, float y, float w, float h, SkiaSharp.SKColor bg, SkiaSharp.SKColor fg,
                      string text, bool rightAlign, bool bold)
        {
            var rect = new SkiaSharp.SKRect(x, y, x + w, y + h);
            fillPaint.Color = bg;
            canvas.DrawRect(rect, fillPaint);
            canvas.DrawRect(rect, strokePaint);
            var p = bold ? boldPaint : textPaint;
            p.Color = fg;
            float tw = p.MeasureText(text);
            float tx = rightAlign ? x + w - 6f - tw : x + 6f;
            float ty = y + h / 2f + p.TextSize * 0.35f;
            canvas.DrawText(text, tx, ty, p);
        }

        float curY = margin;

        // SETUP header
        DrawCell(margin,              curY, col0,  titleH, headerBg, headerFg, "",       false, true);
        DrawCell(margin + col0,       curY, col12, titleH, headerBg, headerFg, "FRONT",  true,  true);
        DrawCell(margin + col0 + col12, curY, col12, titleH, headerBg, headerFg, "REAR", true,  true);
        curY += titleH;

        foreach (var (label, frontVal, rearVal) in settingRows)
        {
            DrawCell(margin,                curY, col0,  rowH, cellBg, cellFg, label,          false, false);
            DrawCell(margin + col0,         curY, col12, rowH, cellBg, cellFg, frontVal ?? "-", true,  false);
            DrawCell(margin + col0 + col12, curY, col12, rowH, cellBg, cellFg, rearVal  ?? "-", true,  false);
            curY += rowH;
        }

        // Notes description
        if (hasDescription)
        {
            curY += sectionGap;
            DrawCell(margin, curY, contentWidth, titleH, headerBg, headerFg, "NOTES", false, true);
            curY += titleH;

            var noteRect = new SkiaSharp.SKRect(margin, curY, margin + contentWidth, curY + noteLines.Length * (noteFontSize + 4f) + 16f);
            fillPaint.Color = cellBg;
            canvas.DrawRect(noteRect, fillPaint);
            canvas.DrawRect(noteRect, strokePaint);

            notePaint.Color = cellFg;
            float lineY = curY + 8f + noteFontSize;
            foreach (var line in noteLines)
            {
                canvas.DrawText(line, margin + 8f, lineY, notePaint);
                lineY += noteFontSize + 4f;
            }
        }

        document.EndPage();
    }

    private string RenderSvgsToPdf(List<string> svgXmlList, SummaryPageViewModel summary, NotesPageViewModel notes)
    {
        var tempDir = System.IO.Path.GetTempPath();
        // Strip characters that are invalid in filenames or URLs (space, #, %, &, etc.)
        var sanitizedName = System.Text.RegularExpressions.Regex.Replace(Name ?? "session", @"[^\w\-.]", "_");
        var pdfPath = System.IO.Path.Combine(tempDir, $"{sanitizedName}.pdf");

        // Parse all SVGs in parallel (expensive XML + Skia picture recording),
        // then write PDF pages sequentially (SKDocument is not thread-safe).
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

            DrawSummaryPage(document, summary, (float)LastKnownBounds.Width);

            foreach (var svg in svgObjects)
            {
                var picture = svg.Picture;
                if (picture is null) continue;

                var bounds = picture.CullRect;
                using var canvas = document.BeginPage(bounds.Width, bounds.Height);
                canvas.DrawPicture(picture);
                document.EndPage();
            }

            DrawNotesPage(document, notes, (float)LastKnownBounds.Width);

            document.Close();
        }
        finally
        {
            foreach (var svg in svgObjects)
                svg.Dispose();
        }

        return pdfPath;
    }

    #endregion
}
