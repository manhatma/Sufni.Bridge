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
    private const int CurrentPlotVersion = 213;

    // Approximate rendered height of the VelocityBandView control (margin + title text +
    // 44 px band grid). Used to size the low-speed velocity histograms so the
    // histogram+bands pair matches a full normal histogram.
    private const int VelocityBandViewHeight = 70;

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

    private void ShareBalanceMetricsWithSummary() =>
        SummaryPage.EffectiveHeadAngle = BalancePage.Metrics.EffectiveHeadAngle;
    public CropPageViewModel CropPage { get; } = new();

    // Shared session-wide time-zoom state, bound by the TimeZoomControl on the Spring/Damper/Misc
    // pages; one instance keeps the window in sync across all three. See the time-zoom region below.
    private readonly TimeZoomViewModel _timeZoom = new();
    private TelemetryData? _analysisData;
    private CancellationTokenSource? _zoomRenderCts;
    private SvgImage? _fullFrontTravel, _fullRearTravel, _fullFrontVelocity, _fullRearVelocity, _fullFrontAccel, _fullRearAccel;
    private bool _timeZoomSnapshotTaken;

    private NotesPageViewModel NotesPage { get; } = new();
    public ObservableCollection<PageViewModelBase> Pages { get; }
    public string Description => NotesPage.Description ?? "";
    public override bool IsComplete => session.HasProcessedData;
    public override bool ShowPdfExportButton => true;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool isGeneratingPdf;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool isAnalyzingData;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool isCombinedSession;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool isExpanded;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool isCropVisible;
    public ObservableCollection<SessionViewModel> SubSessions { get; } = [];

    public int NestingDepth => IsCombinedSession && SubSessions.Count > 0
        ? SubSessions.Max(s => s.NestingDepth) + 1
        : 1;

    public double ChainIconAngle => IsExpanded ? 0 : 90;

    [RelayCommand]
    private void ToggleExpand()
    {
        if (!IsCombinedSession) return;
        IsExpanded = !IsExpanded;
        OnPropertyChanged(nameof(ChainIconAngle));
    }

    // Toolbar commands that switch meaning when crop overlay is open
    public System.Windows.Input.ICommand ContextSaveCommand  => IsCropVisible ? CropPage.ApplyCropCommand! : SaveCommand;
    public System.Windows.Input.ICommand ContextResetCommand => IsCropVisible ? CropPage.ResetCropCommand! : ResetCommand;
    public string SaveLabel => IsCropVisible ? (CropPage.IsModified ? "apply" : "cancel") : "save";

    partial void OnIsCropVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(ContextSaveCommand));
        OnPropertyChanged(nameof(ContextResetCommand));
        OnPropertyChanged(nameof(SaveLabel));
    }

    [RelayCommand]
    private void ToggleCropPage()
    {
        IsCropVisible = !IsCropVisible;
    }

    #region Private methods

    // ---- Session-wide time-zoom -------------------------------------------------------------
    //
    // The Spring/Damper/Misc pages each host a TimeZoomControl bound to the shared _timeZoom. When
    // the user picks a 2/5/10 s window and pans it, the six time-series plots (travel, velocity,
    // acceleration × front/rear) re-render zoomed to that window. Renders are debounced, cancellable
    // and never written to the DB cache — zoom is a transient view state, so no PlotVersion bump.

    // Analysis data actually plotted in the time-series charts: the cropped copy when the session is
    // cropped, else the full data. Derived once from CropPage.FullData and memoized (CreateCroppedCopy
    // re-smooths, so it is not free); _analysisData is nulled whenever the crop changes.
    private TelemetryData? EnsureAnalysisData()
    {
        if (_analysisData is not null) return _analysisData;
        var full = CropPage.FullData;
        if (full is null) return null;
        _analysisData = session.CropStartSample.HasValue && session.CropEndSample.HasValue
            ? full.CreateCroppedCopy(session.CropStartSample.Value, session.CropEndSample.Value)
            : full;
        return _analysisData;
    }

    // Runs once per data-load: EnsureAnalysisData sets _analysisData on first success, so later Loaded
    // re-entries skip. Crop apply/reset null _analysisData to force a rebuild.
    private void InitializeTimeZoomIfNeeded()
    {
        if (_analysisData is not null) return;
        InitializeTimeZoom();
    }

    // (Re)initialises the shared zoom state for the current analysis data: session duration, context
    // mini-map, and window reset to full/off. Call on the UI thread.
    private void InitializeTimeZoom()
    {
        _timeZoomSnapshotTaken = false;
        _fullFrontTravel = _fullRearTravel = _fullFrontVelocity = _fullRearVelocity = _fullFrontAccel = _fullRearAccel = null;

        var data = EnsureAnalysisData();
        var len = data is null ? 0
            : data.Front.Present ? data.Front.Travel.Length
            : data.Rear.Present ? data.Rear.Travel.Length : 0;
        var rate = data?.SampleRate ?? 0;

        if (data is null || len == 0 || rate <= 0)
        {
            _timeZoom.IsEnabled = false;
            return;
        }

        var duration = len / (double)rate;
        _timeZoom.WindowSeconds = 0;
        _timeZoom.StartSeconds = 0;
        _timeZoom.TotalDurationSeconds = duration;
        _timeZoom.IsEnabled = duration > 2.0;   // needs room for at least the smallest (2 s) window

        GenerateMiniMap(data);
    }

    // Renders the full-session context strip (front+rear travel over time) that the TimeZoomControl
    // overlays the highlight band on. Uses TravelTimeHistoryPlot so its PixelPadding(55,14,50,40)
    // matches the control's overlay Margin(55,40,14,50). Background thread → posts to _timeZoom.
    private void GenerateMiniMap(TelemetryData data)
    {
        var b = LastKnownBounds;
        var width = (int)b.Width;
        const double CollapsedTabBarHeight = 30.0;
        var miniHeight = (int)(((b.Height - CollapsedTabBarHeight) * 0.4 + b.Width / 2.0 + CollapsedTabBarHeight) / 2.0);
        if (width <= 0 || miniHeight <= 0) return;

        Task.Run(() =>
        {
            try
            {
                // One overview strip per domain (travel / velocity / acceleration), each with prominent
                // airtime bands for navigation. The TimeZoomControl on each page shows the matching one.
                var travelSrc = SvgToSource(RenderOverviewXml(new TravelTimeHistoryPlot(new Plot(), showAirtimeBands: true), data, width, miniHeight));
                var velocitySrc = SvgToSource(RenderOverviewXml(new VelocityTimeHistoryPlot(new Plot(), showAirtimeBands: true), data, width, miniHeight));
                var accelSrc = SvgToSource(RenderOverviewXml(new AccelerationTimeHistoryPlot(new Plot(), showAirtimeBands: true), data, width, miniHeight));
                Dispatcher.UIThread.Post(() =>
                {
                    _timeZoom.MiniMapTravel = SourceToImage(travelSrc);
                    _timeZoom.MiniMapVelocity = SourceToImage(velocitySrc);
                    _timeZoom.MiniMapAcceleration = SourceToImage(accelSrc);
                });
            }
            catch
            {
                // Best-effort context strips; the selector/slider still work without them.
            }
        });
    }

    private static string RenderOverviewXml(TelemetryPlot plot, TelemetryData data, int width, int height)
    {
        plot.LoadTelemetryData(data);
        plot.Plot.Axes.Title.Label.Text = "Session overview";
        return plot.Plot.GetSvgXml(width, height);
    }

    // Debounced, cancellable reaction to the shared zoom window changing. Off → restore the snapshot;
    // on → schedule a windowed re-render of the six time-series plots.
    private void OnZoomWindowChanged(object? sender, EventArgs e)
    {
        _zoomRenderCts?.Cancel();

        if (!_timeZoom.IsZoomActive)
        {
            RestoreFullTimePlots();
            return;
        }

        SnapshotFullTimePlotsIfNeeded();

        var cts = new CancellationTokenSource();
        _zoomRenderCts = cts;
        var token = cts.Token;
        var winStart = _timeZoom.StartSeconds;
        var winEnd = _timeZoom.WindowEndSeconds;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(280, token);   // settle after the last pan/selection
                if (token.IsCancellationRequested) return;
                RenderTimePlotsForWindow(winStart, winEnd, token);
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    // Captures the current full-range time-plot images once (before the first windowed overwrite) so
    // RestoreFullTimePlots can put them back instantly on reset. UI thread.
    private void SnapshotFullTimePlotsIfNeeded()
    {
        if (_timeZoomSnapshotTaken) return;
        _fullFrontTravel   = SpringPage.FrontTravelTimeCropped;
        _fullRearTravel    = SpringPage.RearTravelTimeCropped;
        _fullFrontVelocity = DamperPage.FrontVelocityTimeCropped;
        _fullRearVelocity  = DamperPage.RearVelocityTimeCropped;
        _fullFrontAccel    = MiscPage.FrontAccelerationTimeCropped;
        _fullRearAccel     = MiscPage.RearAccelerationTimeCropped;
        _timeZoomSnapshotTaken = true;
    }

    private void RestoreFullTimePlots()
    {
        if (!_timeZoomSnapshotTaken) return;
        Dispatcher.UIThread.Post(() =>
        {
            SpringPage.FrontTravelTimeCropped     = _fullFrontTravel;
            SpringPage.RearTravelTimeCropped      = _fullRearTravel;
            DamperPage.FrontVelocityTimeCropped   = _fullFrontVelocity;
            DamperPage.RearVelocityTimeCropped    = _fullRearVelocity;
            MiscPage.FrontAccelerationTimeCropped = _fullFrontAccel;
            MiscPage.RearAccelerationTimeCropped  = _fullRearAccel;
            SpringPage.CombinedTravelTimeZoomed     = null;
            DamperPage.CombinedVelocityTimeZoomed   = null;
            MiscPage.CombinedAccelerationTimeZoomed = null;
        });
    }

    // Renders the six time-series plots zoomed to [winStart, winEnd] from the in-memory analysis data.
    // Background thread; each plot is posted to its page as it finishes, with the token checked between
    // plots so a superseding pan abandons stale work. Does not touch the DB cache.
    private void RenderTimePlotsForWindow(double winStart, double winEnd, CancellationToken token)
    {
        var data = _analysisData;
        if (data is null) return;

        var b = LastKnownBounds;
        var (width, height) = ((int)b.Width, (int)(b.Height / 2.0));
        if (width <= 0 || height <= 0) return;

        void Render(Func<string> makeSvg, Action<SvgImage?> assign)
        {
            if (token.IsCancellationRequested) return;
            var src = SvgToSource(makeSvg());
            if (token.IsCancellationRequested) return;
            Dispatcher.UIThread.Post(() => { if (!token.IsCancellationRequested) assign(SourceToImage(src)); });
        }

        // Each domain (travel / velocity / acceleration) is shown as ONE combined front+rear plot
        // while zoomed; the separate per-side plots are hidden by nulling them (IsNotNull bindings).
        if (data.Front.Present || data.Rear.Present)
        {
            Render(() => { var p = new TravelTimeCombinedPlot(new Plot(), winStart, winEnd); p.LoadTelemetryData(data); return p.Plot.GetSvgXml(width, height); },
                   img => { SpringPage.CombinedTravelTimeZoomed = img; SpringPage.FrontTravelTimeCropped = null; SpringPage.RearTravelTimeCropped = null; });

            Render(() => { var p = new VelocityTimeCombinedPlot(new Plot(), winStart, winEnd); p.LoadTelemetryData(data); return p.Plot.GetSvgXml(width, height); },
                   img => { DamperPage.CombinedVelocityTimeZoomed = img; DamperPage.FrontVelocityTimeCropped = null; DamperPage.RearVelocityTimeCropped = null; });

            Render(() => { var p = new AccelerationTimeCombinedPlot(new Plot(), winStart, winEnd); p.LoadTelemetryData(data); return p.Plot.GetSvgXml(width, height); },
                   img => { MiscPage.CombinedAccelerationTimeZoomed = img; MiscPage.FrontAccelerationTimeCropped = null; MiscPage.RearAccelerationTimeCropped = null; });
        }
    }

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

        // Cache is stale when crop boundaries differ from what was cached
        if (cache.CropStartSample != session.CropStartSample ||
            cache.CropEndSample   != session.CropEndSample)
        {
            return (false, false, false);
        }

        // Cache is stale when the balance-target overrides (per discipline) no longer imply the
        // expected pitch band that was baked into the PitchBalance SVG — the μ row re-colors
        // live from the current overrides, so a stale band would contradict it in the same view.
        if (cache.PitchBalance is not null && cache.BalanceMetricsJson is not null)
        {
            try
            {
                var m = JsonSerializer.Deserialize<BalanceMetrics>(cache.BalanceMetricsJson);
                if (m is not null && !PitchBandMatchesCache(await ComputeExpectedPitchBandAsync(m), cache))
                {
                    return (false, false, false);
                }
            }
            catch
            {
                // Corrupt metrics cache — the completeness checks below trigger a rebuild anyway
            }
        }

        // Load TravelTimeHistory (full data, always in cache)
        if (cache.TravelTimeHistory is not null)
        {
            var tthSrc = await Task.Run(() => SvgToSource(cache.TravelTimeHistory));
            CropPage.TravelTimeHistory = SourceToImage(tthSrc);
        }

        var hasVdc = cache.VelocityDistributionComparison is not null;

        // Combined sessions never get the phase-portrait plots cached (CreateCache skips them
        // and leaves the columns null by design) — treat that as "complete" rather than stale,
        // or the cache would never be considered valid and CreateCache would rerun on every open.
        var isCombined = (await databaseService.GetCombinedSourcesAsync(Id)).Count > 0;
        var hasPvc = isCombined || cache.PositionVelocityComparison is not null;

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
                    SummaryPage.Airtime = summary.Airtime;
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
                var rearDamperVelHistTask = Task.Run(() => SvgToSource(cache.RearDamperVelocityHistogram));
                var rearLsVelHistTask  = Task.Run(() => SvgToSource(cache.RearLowSpeedVelocityHistogram));
                var combBalTask      = Task.Run(() => SvgToSource(cache.CombinedBalance));
                var compBalTask      = Task.Run(() => SvgToSource(cache.CompressionBalance));
                var rebBalTask       = Task.Run(() => SvgToSource(cache.ReboundBalance));
                var velDistCompTask  = Task.Run(() => SvgToSource(cache.VelocityDistributionComparison));
                var posVelCompTask   = Task.Run(() => SvgToSource(cache.PositionVelocityComparison));
                var frontPosVelTask  = Task.Run(() => SvgToSource(cache.FrontPositionVelocity));
                var rearPosVelTask   = Task.Run(() => SvgToSource(cache.RearPositionVelocity));
                var frontTravelCropTask = Task.Run(() => SvgToSource(cache.FrontTravelTimeCropped));
                var rearTravelCropTask  = Task.Run(() => SvgToSource(cache.RearTravelTimeCropped));
                var frontVelCropTask    = Task.Run(() => SvgToSource(cache.FrontVelocityTimeCropped));
                var rearVelCropTask     = Task.Run(() => SvgToSource(cache.RearVelocityTimeCropped));
                var frontAccelCropTask  = Task.Run(() => SvgToSource(cache.FrontAccelerationTimeCropped));
                var rearAccelCropTask   = Task.Run(() => SvgToSource(cache.RearAccelerationTimeCropped));
                var combinedFftTask     = Task.Run(() => SvgToSource(cache.CombinedTravelFft));
                var combinedFftHighTask = Task.Run(() => SvgToSource(cache.CombinedTravelFftHigh));
                var combinedVelFftTask  = Task.Run(() => SvgToSource(cache.CombinedVelocityFft));
                var pitchBalanceTask    = Task.Run(() => SvgToSource(cache.PitchBalance));
                var pitchCoherenceTask  = Task.Run(() => SvgToSource(cache.PitchCoherence));
                var goutScatterTask     = Task.Run(() => SvgToSource(cache.GoutScatter));
                var cumulativeTravelTask = Task.Run(() => SvgToSource(cache.CumulativeTravel));

                await Task.WhenAll(frontVelHistTask, frontLsVelHistTask, rearVelHistTask, rearDamperVelHistTask, rearLsVelHistTask,
                    combBalTask, compBalTask, rebBalTask,
                    velDistCompTask, posVelCompTask, frontPosVelTask, rearPosVelTask,
                    frontTravelCropTask, rearTravelCropTask, frontVelCropTask, rearVelCropTask,
                    frontAccelCropTask, rearAccelCropTask,
                    combinedFftTask, combinedFftHighTask,
                    pitchBalanceTask, pitchCoherenceTask, goutScatterTask, cumulativeTravelTask);

                var frontVelHistSrc   = frontVelHistTask.Result;
                var frontLsVelHistSrc = frontLsVelHistTask.Result;
                var rearVelHistSrc    = rearVelHistTask.Result;
                var rearDamperVelHistSrc = rearDamperVelHistTask.Result;
                var rearLsVelHistSrc  = rearLsVelHistTask.Result;
                var combBalSrc      = combBalTask.Result;
                var compBalSrc      = compBalTask.Result;
                var rebBalSrc       = rebBalTask.Result;
                var velDistCompSrc  = velDistCompTask.Result;
                var posVelCompSrc   = posVelCompTask.Result;
                var frontPosVelSrc  = frontPosVelTask.Result;
                var rearPosVelSrc   = rearPosVelTask.Result;

                // Resolve discipline up-front (async DB lookup) — the UI-thread
                // lambda below is sync and can't await.
                var sessionDiscipline = cache.BalanceMetricsJson is not null
                    ? await GetSessionDisciplineAsync()
                    : null;
                var balanceOverrides = await GetBalanceOverridesAsync(sessionDiscipline);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    DamperPage.FrontVelocityHistogram          = SourceToImage(frontVelHistSrc);
                    DamperPage.FrontLowSpeedVelocityHistogram = SourceToImage(frontLsVelHistSrc);
                    DamperPage.RearVelocityHistogram           = SourceToImage(rearVelHistSrc);
                    DamperPage.RearDamperVelocityHistogram     = SourceToImage(rearDamperVelHistSrc);
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
                        BalancePage.CombinedTravelFft     = SourceToImage(combinedFftTask.Result);
                        BalancePage.CombinedTravelFftHigh = SourceToImage(combinedFftHighTask.Result);
                        BalancePage.CombinedVelocityFft   = SourceToImage(combinedVelFftTask.Result);
                        BalancePage.PitchBalance          = SourceToImage(pitchBalanceTask.Result);
                        BalancePage.PitchCoherence        = SourceToImage(pitchCoherenceTask.Result);
                        BalancePage.GoutScatter           = SourceToImage(goutScatterTask.Result);
                        BalancePage.CumulativeTravel      = SourceToImage(cumulativeTravelTask.Result);
                        if (cache.BalanceMetricsJson is not null)
                        {
                            try
                            {
                                var m = JsonSerializer.Deserialize<BalanceMetrics>(cache.BalanceMetricsJson);
                                if (m is not null) BalancePage.Metrics.Apply(m, sessionDiscipline, balanceOverrides);
                            }
                            catch { /* corrupt metrics cache; will be rebuilt */ }
                        }
                    }
                    else
                    {
                        Pages.Remove(BalancePage);
                    }

                    DamperPage.VelocityDistributionComparison = SourceToImage(velDistCompSrc);
                    SpringPage.FrontTravelTimeCropped         = SourceToImage(frontTravelCropTask.Result);
                    SpringPage.RearTravelTimeCropped          = SourceToImage(rearTravelCropTask.Result);
                    DamperPage.FrontVelocityTimeCropped       = SourceToImage(frontVelCropTask.Result);
                    DamperPage.RearVelocityTimeCropped        = SourceToImage(rearVelCropTask.Result);
                    MiscPage.PositionVelocityComparison       = SourceToImage(posVelCompSrc);
                    MiscPage.FrontPositionVelocity            = SourceToImage(frontPosVelSrc);
                    MiscPage.RearPositionVelocity             = SourceToImage(rearPosVelSrc);
                    MiscPage.FrontAccelerationTimeCropped     = SourceToImage(frontAccelCropTask.Result);
                    MiscPage.RearAccelerationTimeCropped      = SourceToImage(rearAccelCropTask.Result);
                });
            });
        }

        return (true, hasVdc, hasPvc);
    }

    /// <summary>
    /// Resolves the discipline of the Setup that owns this session, or null if the
    /// setup is missing/unreadable. Used by the balance metrics box to pick
    /// discipline-specific eigenfrequency target bands.
    /// </summary>
    private async Task<Discipline?> GetSessionDisciplineAsync()
    {
        if (!session.Setup.HasValue) return null;
        var dbSvc = App.Current?.Services?.GetService<IDatabaseService>();
        if (dbSvc is null) return null;
        try
        {
            var setup = await dbSvc.GetSetupAsync(session.Setup.Value);
            return setup?.Discipline;
        }
        catch { return null; }
    }

    /// <summary>
    /// Loads the user's per-discipline balance-target overrides as a metric-keyed map, or
    /// null when there is no discipline / database. Passed into BalanceMetrics.Apply so the
    /// metric table reflects the user's edited green ranges.
    /// </summary>
    private async Task<Dictionary<string, (double? min, double? max)>?> GetBalanceOverridesAsync(Discipline? discipline)
    {
        if (discipline is null) return null;
        var dbSvc = App.Current?.Services?.GetService<IDatabaseService>();
        if (dbSvc is null) return null;
        try
        {
            var overrides = await dbSvc.GetBalanceTargetOverridesAsync(discipline.Value);
            return overrides.ToDictionary(o => o.MetricKey, o => (o.GreenMin, o.GreenMax));
        }
        catch { return null; }
    }

    // Expected pitch band implied by the CURRENT per-discipline overrides and the session's
    // cached geometry — the counterpart to the band signature stored in session_cache.
    private async Task<(double minDeg, double maxDeg)?> ComputeExpectedPitchBandAsync(BalanceMetrics m)
    {
        var discipline = await GetSessionDisciplineAsync();
        var overrides = await GetBalanceOverridesAsync(discipline);
        return BalanceTargetDefaults.ExpectedPitchBand(
            BalanceTargetDefaults.EffectiveGreen(overrides, "FrontSag", discipline),
            BalanceTargetDefaults.EffectiveGreen(overrides, "RearSag", discipline),
            m.MaxFrontTravelMm, m.MaxRearTravelMm, m.WheelbaseMm);
    }

    private static bool PitchBandMatchesCache((double minDeg, double maxDeg)? band, SessionCache cache)
    {
        static bool Eq(double? a, double? b) =>
            (a is null && b is null) || (a.HasValue && b.HasValue && Math.Abs(a.Value - b.Value) < 1e-9);
        return Eq(band?.minDeg, cache.PitchExpectedMinDeg) && Eq(band?.maxDeg, cache.PitchExpectedMaxDeg);
    }

    private static Task ThrottledPlotTask(string label, Action work)
    {
        return Task.Run(async () =>
        {
            var waitStart = Stopwatch.GetTimestamp();
            await s_plotSemaphore.WaitAsync();
            var waitMs = Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds;
            if (waitMs > 1.0) PerfLog.Log($"plotwait/{label}", waitMs);
            try
            {
                var workStart = Stopwatch.GetTimestamp();
                work();
                PerfLog.Log($"plot/{label}", Stopwatch.GetElapsedTime(workStart).TotalMilliseconds);
            }
            finally { s_plotSemaphore.Release(); }
        });
    }

    private async Task CreateCache(object? bounds, TelemetryData telemetryData, TelemetryData? fullData = null)
    {
        var swCache = Stopwatch.StartNew();
        var databaseService = App.Current?.Services?.GetService<IDatabaseService>();
        Debug.Assert(databaseService != null, nameof(databaseService) + " != null");

        // Combined sessions have no telemetry of their own (they're a view over their source
        // sessions' data) — the three phase-portrait plots below are skipped for them entirely
        // (cache columns stay null, MiscPageView hides the images via IsVisible bindings).
        var isCombined = (await databaseService.GetCombinedSourcesAsync(Id)).Count > 0;

        var b = (Rect)bounds!;
        var (width, height) = ((int)b.Width, (int)(b.Height / 2.0));
        // Full and cropped time-history share the total vertical budget equally.
        // Previous split: full = height*0.8 (= b.Height*0.4), cropped = width/2.
        // In crop mode the tab bar collapses (~30 px); absorb that into chart heights
        // so the crop sliders keep their absolute Y position.
        const double CollapsedTabBarHeight = 30.0;
        var tthHeight = (int)(((b.Height - CollapsedTabBarHeight) * 0.4 + b.Width / 2.0 + CollapsedTabBarHeight) / 2.0);

        var sessionCache = new SessionCache
        {
            SessionId = Id,
            PlotVersion = CurrentPlotVersion,
            CropStartSample = session.CropStartSample,
            CropEndSample   = session.CropEndSample
        };
        var tasks = new List<Task>();

        // TravelTimeHistory — always uses full (uncompressed) data
        var tthSource = fullData ?? telemetryData;
        tasks.Add(ThrottledPlotTask("tth", () =>
        {
            var tth = new TravelTimeHistoryPlot(new Plot());
            tth.LoadTelemetryData(tthSource);
            tth.Plot.Axes.Title.Label.Text = "Travel over time (full)";
            sessionCache.TravelTimeHistory = tth.Plot.GetSvgXml(width, tthHeight);
            var tthSrc = SvgToSource(sessionCache.TravelTimeHistory);
            Dispatcher.UIThread.Post(() => { CropPage.TravelTimeHistory = SourceToImage(tthSrc); });
        }));

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
            tasks.Add(ThrottledPlotTask("travelCompHist", () =>
            {
                var tcmp = new TravelHistogramComparisonPlot(new Plot());
                tcmp.LoadTelemetryData(telemetryData);
                sessionCache.TravelComparisonHistogram = tcmp.Plot.GetSvgXml(width, height);
                var travelCompSrc = SvgToSource(sessionCache.TravelComparisonHistogram);
                Dispatcher.UIThread.Post(() => { SpringPage.TravelComparisonHistogram = SourceToImage(travelCompSrc); });
            }));

            tasks.Add(ThrottledPlotTask("frontRearScatter", () =>
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
            tasks.Add(ThrottledPlotTask("frontTravelHist", () =>
            {
                var fth = new TravelHistogramPlot(new Plot(), SuspensionType.Front);
                fth.LoadTelemetryData(telemetryData);
                sessionCache.FrontTravelHistogram = fth.Plot.GetSvgXml(width, height);
                var frontTravelHistSrc = SvgToSource(sessionCache.FrontTravelHistogram);
                Dispatcher.UIThread.Post(() => { SpringPage.FrontTravelHistogram = SourceToImage(frontTravelHistSrc); });
            }));

            tasks.Add(ThrottledPlotTask("frontVelHist", () =>
            {
                var fvh = new VelocityHistogramPlot(new Plot(), SuspensionType.Front);
                fvh.LoadTelemetryData(telemetryData);
                sessionCache.FrontVelocityHistogram = fvh.Plot.GetSvgXml(width, height);
                var frontVelocityHistSrc = SvgToSource(sessionCache.FrontVelocityHistogram);
                Dispatcher.UIThread.Post(() => { DamperPage.FrontVelocityHistogram = SourceToImage(frontVelocityHistSrc); });
            }));

            tasks.Add(ThrottledPlotTask("frontLsVelHist", () =>
            {
                var flsvh = new LowSpeedVelocityHistogramPlot(new Plot(), SuspensionType.Front);
                flsvh.LoadTelemetryData(telemetryData);
                // The low-speed histogram is stacked on top of a fixed-height VelocityBandView
                // (zone breakdown). Subtract that band-view height so that one (histogram +
                // bands) pair fills the same vertical space as one full normal histogram.
                sessionCache.FrontLowSpeedVelocityHistogram = flsvh.Plot.GetSvgXml(width, height - VelocityBandViewHeight);
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
            tasks.Add(ThrottledPlotTask("rearTravelHist", () =>
            {
                var rth = new TravelHistogramPlot(new Plot(), SuspensionType.Rear);
                rth.LoadTelemetryData(telemetryData);
                sessionCache.RearTravelHistogram = rth.Plot.GetSvgXml(width, height);
                var rearTravelHistSrc = SvgToSource(sessionCache.RearTravelHistogram);
                Dispatcher.UIThread.Post(() => { SpringPage.RearTravelHistogram = SourceToImage(rearTravelHistSrc); });
            }));

            tasks.Add(ThrottledPlotTask("rearVelHist", () =>
            {
                var rvh = new VelocityHistogramPlot(new Plot(), SuspensionType.Rear);
                rvh.LoadTelemetryData(telemetryData);
                sessionCache.RearVelocityHistogram = rvh.Plot.GetSvgXml(width, height);
                var rearVelocityHistSrc = SvgToSource(sessionCache.RearVelocityHistogram);
                Dispatcher.UIThread.Post(() => { DamperPage.RearVelocityHistogram = SourceToImage(rearVelocityHistSrc); });
            }));

            tasks.Add(ThrottledPlotTask("rearDamperVelHist", () =>
            {
                var rdvh = new DamperVelocityHistogramPlot(new Plot());
                rdvh.LoadTelemetryData(telemetryData);
                sessionCache.RearDamperVelocityHistogram = rdvh.Plot.GetSvgXml(width, height);
                var rearDamperVelocityHistSrc = SvgToSource(sessionCache.RearDamperVelocityHistogram);
                Dispatcher.UIThread.Post(() => { DamperPage.RearDamperVelocityHistogram = SourceToImage(rearDamperVelocityHistSrc); });
            }));

            tasks.Add(ThrottledPlotTask("rearLsVelHist", () =>
            {
                var rlsvh = new LowSpeedVelocityHistogramPlot(new Plot(), SuspensionType.Rear);
                rlsvh.LoadTelemetryData(telemetryData);
                // See Front counterpart for the height-minus-band-view rationale.
                sessionCache.RearLowSpeedVelocityHistogram = rlsvh.Plot.GetSvgXml(width, height - VelocityBandViewHeight);
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
            tasks.Add(ThrottledPlotTask("combinedBalance", () =>
            {
                var combined = new CombinedBalancePlot(new Plot());
                combined.LoadTelemetryData(telemetryData);
                sessionCache.CombinedBalance = combined.Plot.GetSvgXml(width, height);
                var combinedBalanceSrc = SvgToSource(sessionCache.CombinedBalance);
                Dispatcher.UIThread.Post(() => { BalancePage.CombinedBalance = SourceToImage(combinedBalanceSrc); });
            }));

            tasks.Add(ThrottledPlotTask("compressionBalance", () =>
            {
                var cb = new BalancePlot(new Plot(), BalanceType.Compression);
                cb.LoadTelemetryData(telemetryData);
                sessionCache.CompressionBalance = cb.Plot.GetSvgXml(width, height);
                var compressionBalanceSrc = SvgToSource(sessionCache.CompressionBalance);
                Dispatcher.UIThread.Post(() => { BalancePage.CompressionBalance = SourceToImage(compressionBalanceSrc); });
            }));

            tasks.Add(ThrottledPlotTask("reboundBalance", () =>
            {
                var rb = new BalancePlot(new Plot(), BalanceType.Rebound);
                rb.LoadTelemetryData(telemetryData);
                sessionCache.ReboundBalance = rb.Plot.GetSvgXml(width, height);
                var reboundBalanceSrc = SvgToSource(sessionCache.ReboundBalance);
                Dispatcher.UIThread.Post(() => { BalancePage.ReboundBalance = SourceToImage(reboundBalanceSrc); });
            }));

            tasks.Add(ThrottledPlotTask("cumulativeTravel", () =>
            {
                var ct = new CumulativeTravelPlot(new Plot());
                ct.LoadTelemetryData(telemetryData);
                sessionCache.CumulativeTravel = ct.Plot.GetSvgXml(width, height);
                var cumulativeTravelSrc = SvgToSource(sessionCache.CumulativeTravel);
                Dispatcher.UIThread.Post(() => { BalancePage.CumulativeTravel = SourceToImage(cumulativeTravelSrc); });
            }));
        }
        else
        {
            Dispatcher.UIThread.Post(() => { Pages.Remove(BalancePage); });
        }

        if (telemetryData.Front.Present)
        {
            tasks.Add(ThrottledPlotTask("frontTravelTimeCropped", () =>
            {
                var ttc = new TravelTimeCroppedPlot(new Plot(), SuspensionType.Front);
                ttc.LoadTelemetryData(telemetryData);
                sessionCache.FrontTravelTimeCropped = ttc.Plot.GetSvgXml(width, height);
                var src = SvgToSource(sessionCache.FrontTravelTimeCropped);
                Dispatcher.UIThread.Post(() => { SpringPage.FrontTravelTimeCropped = SourceToImage(src); });
            }));

            tasks.Add(ThrottledPlotTask("frontVelTimeCropped", () =>
            {
                var vtc = new VelocityTimeCroppedPlot(new Plot(), SuspensionType.Front);
                vtc.LoadTelemetryData(telemetryData);
                sessionCache.FrontVelocityTimeCropped = vtc.Plot.GetSvgXml(width, height);
                var src = SvgToSource(sessionCache.FrontVelocityTimeCropped);
                Dispatcher.UIThread.Post(() => { DamperPage.FrontVelocityTimeCropped = SourceToImage(src); });
            }));

        }

        if (telemetryData.Rear.Present)
        {
            tasks.Add(ThrottledPlotTask("rearTravelTimeCropped", () =>
            {
                var ttc = new TravelTimeCroppedPlot(new Plot(), SuspensionType.Rear);
                ttc.LoadTelemetryData(telemetryData);
                sessionCache.RearTravelTimeCropped = ttc.Plot.GetSvgXml(width, height);
                var src = SvgToSource(sessionCache.RearTravelTimeCropped);
                Dispatcher.UIThread.Post(() => { SpringPage.RearTravelTimeCropped = SourceToImage(src); });
            }));

            tasks.Add(ThrottledPlotTask("rearVelTimeCropped", () =>
            {
                var vtc = new VelocityTimeCroppedPlot(new Plot(), SuspensionType.Rear);
                vtc.LoadTelemetryData(telemetryData);
                sessionCache.RearVelocityTimeCropped = vtc.Plot.GetSvgXml(width, height);
                var src = SvgToSource(sessionCache.RearVelocityTimeCropped);
                Dispatcher.UIThread.Post(() => { DamperPage.RearVelocityTimeCropped = SourceToImage(src); });
            }));

        }

        // Combined Front+Rear FFT and Balance metrics — only when both sides are present.
        if (telemetryData.Front.Present && telemetryData.Rear.Present)
        {
            tasks.Add(ThrottledPlotTask("velFft", () =>
            {
                var velFft = new CombinedTravelFftPlot(new Plot(), minHz: 1.0, maxHz: 10.0,
                    peakMinHz: 1.3, peakMaxHz: 4.5,
                    fitYAxisToData: true, topHeadroomDb: 2.0,
                    mode: WheelSpectrumMode.Velocity);
                velFft.LoadTelemetryData(telemetryData);
                sessionCache.CombinedVelocityFft = velFft.Plot.GetSvgXml(width, height);
                var src = SvgToSource(sessionCache.CombinedVelocityFft);
                Dispatcher.UIThread.Post(() => { BalancePage.CombinedVelocityFft = SourceToImage(src); });
            }));

            tasks.Add(ThrottledPlotTask("travelFft", () =>
            {
                var fft = new CombinedTravelFftPlot(new Plot(), minHz: 1.0, maxHz: 10.0,
                    peakMinHz: 1.3, peakMaxHz: 4.5,
                    fitYAxisToData: true, topHeadroomDb: 3.0);
                fft.LoadTelemetryData(telemetryData);
                sessionCache.CombinedTravelFft = fft.Plot.GetSvgXml(width, height);
                var src = SvgToSource(sessionCache.CombinedTravelFft);
                Dispatcher.UIThread.Post(() => { BalancePage.CombinedTravelFft = SourceToImage(src); });
            }));

            // Second FFT view zoomed to the higher-frequency range (3–100 Hz). Peak
            // markers off — body resonance lives below 3 Hz so the search would land
            // on noise.
            tasks.Add(ThrottledPlotTask("travelFftHigh", () =>
            {
                var fftHigh = new CombinedTravelFftPlot(new Plot(), minHz: 10.0, maxHz: 100.0,
                    peakMinHz: 0.0, peakMaxHz: 0.0, segmentLength: 4096, fitYAxisToData: true,
                    lineWidth: 1.5f);
                fftHigh.LoadTelemetryData(telemetryData);
                sessionCache.CombinedTravelFftHigh = fftHigh.Plot.GetSvgXml(width, height);
                var src = SvgToSource(sessionCache.CombinedTravelFftHigh);
                Dispatcher.UIThread.Post(() => { BalancePage.CombinedTravelFftHigh = SourceToImage(src); });
            }));

            tasks.Add(Task.Run(async () =>
            {
                var discipline = await GetSessionDisciplineAsync();
                // Refresh Wheelbase from the live linkage row — the Linkage carried
                // in telemetryData comes from the MessagePack blob, whose Id is
                // [IgnoreMember] and is regenerated on deserialization. Resolve the
                // real linkage via session.Setup → Setup.LinkageId.
                if (telemetryData.Linkage is { } lk && lk.Wheelbase is null or 0 && session.Setup.HasValue)
                {
                    var setup = await databaseService.GetSetupAsync(session.Setup.Value);
                    if (setup is not null)
                    {
                        var liveLinkage = await databaseService.GetLinkageAsync(setup.LinkageId);
                        if (liveLinkage?.Wheelbase is > 0) lk.Wheelbase = liveLinkage.Wheelbase;
                    }
                }
                var metrics = telemetryData.CalculateBalanceMetrics(discipline);
                sessionCache.BalanceMetricsJson = JsonSerializer.Serialize(metrics);
                var balanceOverrides = await GetBalanceOverridesAsync(discipline);
                Dispatcher.UIThread.Post(() => BalancePage.Metrics.Apply(metrics, discipline, balanceOverrides));

                // Pitch-attitude plots (lag-corrected). The expected band comes from the effective
                // SAG green ranges so the plot's reference matches the μ metric's traffic light.
                // Wheelbase was refreshed just above, so CalculatePitchDegrees sees it.
                var expectedBand = BalanceTargetDefaults.ExpectedPitchBand(
                    BalanceTargetDefaults.EffectiveGreen(balanceOverrides, "FrontSag", discipline),
                    BalanceTargetDefaults.EffectiveGreen(balanceOverrides, "RearSag", discipline),
                    metrics.MaxFrontTravelMm, metrics.MaxRearTravelMm, metrics.WheelbaseMm);
                // Band signature for LoadCache's staleness check — the band is baked into the
                // PitchBalance SVG below, so a later per-discipline override edit must be able
                // to invalidate this cache row.
                sessionCache.PitchExpectedMinDeg = expectedBand?.minDeg;
                sessionCache.PitchExpectedMaxDeg = expectedBand?.maxDeg;

                await Task.WhenAll(
                    ThrottledPlotTask("pitchBalance", () =>
                    {
                        var pb = new PitchBalancePlot(new Plot(), expectedBand?.minDeg, expectedBand?.maxDeg);
                        pb.LoadTelemetryData(telemetryData);
                        sessionCache.PitchBalance = pb.Plot.GetSvgXml(width, height);
                        var src = SvgToSource(sessionCache.PitchBalance);
                        Dispatcher.UIThread.Post(() => { BalancePage.PitchBalance = SourceToImage(src); });
                    }),
                    ThrottledPlotTask("pitchCoherence", () =>
                    {
                        var pc = new PitchCoherencePlot(new Plot(), discipline);
                        pc.LoadTelemetryData(telemetryData);
                        sessionCache.PitchCoherence = pc.Plot.GetSvgXml(width, height);
                        var src = SvgToSource(sessionCache.PitchCoherence);
                        Dispatcher.UIThread.Post(() => { BalancePage.PitchCoherence = SourceToImage(src); });
                    }),
                    ThrottledPlotTask("goutScatter", () =>
                    {
                        var gs = new GoutScatterPlot(new Plot());
                        gs.LoadTelemetryData(telemetryData);
                        sessionCache.GoutScatter = gs.Plot.GetSvgXml(width, height);
                        var src = SvgToSource(sessionCache.GoutScatter);
                        Dispatcher.UIThread.Post(() => { BalancePage.GoutScatter = SourceToImage(src); });
                    }));
            }));
        }

        if (telemetryData.Front.Present)
        {
            tasks.Add(ThrottledPlotTask("frontAccel", () =>
            {
                var atc = new AccelerationTimeCroppedPlot(new Plot(), SuspensionType.Front);
                atc.LoadTelemetryData(telemetryData);
                sessionCache.FrontAccelerationTimeCropped = atc.Plot.GetSvgXml(width, height);
                var src = SvgToSource(sessionCache.FrontAccelerationTimeCropped);
                Dispatcher.UIThread.Post(() => { MiscPage.FrontAccelerationTimeCropped = SourceToImage(src); });
            }));
        }

        if (telemetryData.Rear.Present)
        {
            tasks.Add(ThrottledPlotTask("rearAccel", () =>
            {
                var atc = new AccelerationTimeCroppedPlot(new Plot(), SuspensionType.Rear);
                atc.LoadTelemetryData(telemetryData);
                sessionCache.RearAccelerationTimeCropped = atc.Plot.GetSvgXml(width, height);
                var src = SvgToSource(sessionCache.RearAccelerationTimeCropped);
                Dispatcher.UIThread.Post(() => { MiscPage.RearAccelerationTimeCropped = SourceToImage(src); });
            }));
        }

        tasks.Add(ThrottledPlotTask("velDistComp", () =>
        {
            var vdc = new VelocityDistributionComparisonPlot(new Plot());
            vdc.LoadTelemetryData(telemetryData);
            sessionCache.VelocityDistributionComparison = vdc.Plot.GetSvgXml(width, height);
            var velDistCompSrc = SvgToSource(sessionCache.VelocityDistributionComparison);
            Dispatcher.UIThread.Post(() => { DamperPage.VelocityDistributionComparison = SourceToImage(velDistCompSrc); });
        }));

        // Combined sessions skip all three phase-portrait plots: cache columns stay null and
        // MiscPageView hides the corresponding images via its IsVisible bindings.
        if (isCombined)
        {
            Dispatcher.UIThread.Post(() =>
            {
                MiscPage.PositionVelocityComparison = null;
                MiscPage.FrontPositionVelocity = null;
                MiscPage.RearPositionVelocity = null;
            });
        }
        else
        {
            tasks.Add(ThrottledPlotTask("posVelComp", () =>
            {
                var pvc = new PositionVelocityComparisonPlot(new Plot());
                pvc.LoadTelemetryData(telemetryData);
                sessionCache.PositionVelocityComparison = pvc.Plot.GetSvgXml(width, height);
                var posVelCompSrc = SvgToSource(sessionCache.PositionVelocityComparison);
                Dispatcher.UIThread.Post(() => { MiscPage.PositionVelocityComparison = SourceToImage(posVelCompSrc); });
            }));

            if (telemetryData.Front.Present)
            {
                tasks.Add(ThrottledPlotTask("frontPosVel", () =>
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
                tasks.Add(ThrottledPlotTask("rearPosVel", () =>
                {
                    var rpv = new PositionVelocityPlot(new Plot(), SuspensionType.Rear);
                    rpv.LoadTelemetryData(telemetryData);
                    sessionCache.RearPositionVelocity = rpv.Plot.GetSvgXml(width, height);
                    var rearPosVelSrc = SvgToSource(sessionCache.RearPositionVelocity);
                    Dispatcher.UIThread.Post(() => { MiscPage.RearPositionVelocity = SourceToImage(rearPosVelSrc); });
                }));
            }
        }

        // Summary runs concurrently with all plots (reuses shared VelocityBands tasks)
        tasks.Add(Task.Run(async () =>
        {
            var summaryData = await PopulateSummary(telemetryData, frontBandsTask, rearBandsTask);
            sessionCache.SummaryJson = JsonSerializer.Serialize(summaryData);
        }));

        await Task.WhenAll(tasks);

        await databaseService.PutSessionCacheAsync(sessionCache);
        PerfLog.Log("cache/total", swCache.Elapsed.TotalMilliseconds);
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
        string[][] WheelRows,
        // Em dash, matching the placeholder BalancePageViewModel uses for an unknown metric —
        // the two sit next to each other in the Summary tab's run-data grid.
        string Airtime = "—");

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

    private static string FormatCumulativeTravel(TelemetryData telemetryData, SuspensionType type)
    {
        var cum = telemetryData.CalculateCumulativeTravel(type);
        if (cum.Length == 0)
        {
            return "-";
        }

        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{cum[^1] / 1000.0:F1}");
    }

    private static string FormatAirtime(Airtime[]? airtimes)
    {
        if (airtimes is null)
        {
            return "—";
        }

        var total = airtimes.Sum(a => a.End - a.Start);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{total:0.0} s ({airtimes.Length}×)");
    }

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
        var date = (Timestamp ?? DateTime.UnixEpoch).ToString("dd-MM-yyyy");
        var time = (Timestamp ?? DateTime.UnixEpoch).ToString("HH:mm");
        var sampleCount = Math.Max(telemetryData.Front.Travel?.Length ?? 0, telemetryData.Rear.Travel?.Length ?? 0);
        var duration = telemetryData.SampleRate > 0
            ? TimeSpan.FromSeconds(sampleCount / (double)telemetryData.SampleRate)
            : TimeSpan.Zero;
        var runDuration = duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss")
            : duration.ToString(@"m\:ss");

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
                rearBands is null ? "-" : FormatPercent(rearBands.HighSpeedCompression)),
            new SummaryComparisonRow("Cum. Travel [m]",
                telemetryData.Front.Present ? FormatCumulativeTravel(telemetryData, SuspensionType.Front) : "-",
                telemetryData.Rear.Present ? FormatCumulativeTravel(telemetryData, SuspensionType.Rear) : "-")
        ]);

        var airtime = FormatAirtime(telemetryData.Airtimes);

        Dispatcher.UIThread.Post(() =>
        {
            SummaryPage.RunDataRows = runDataRows;
            SummaryPage.ForkShockRows = forkShockRows;
            SummaryPage.WheelRows = wheelRows;
            SummaryPage.Airtime = airtime;
        });

        return new CachedSummaryData(
            runDataRows.Select(r => new[] { r.Label, r.Value }).ToArray(),
            forkShockRows.Select(r => new[] { r.Label, r.LeftValue, r.RightValue }).ToArray(),
            wheelRows.Select(r => new[] { r.Label, r.LeftValue, r.RightValue }).ToArray(),
            airtime);
    }

    #endregion

    #region Constructors

    public SessionViewModel()
    {
        session = new Session();
        IsInDatabase = false;
        Pages = [SummaryPage, SpringPage, DamperPage, BalancePage, MiscPage, NotesPage];
        SummaryPage.ChangeSetupCommand = new AsyncRelayCommand(HandleSetupReassign);
        ShareBalanceMetricsWithSummary();
        CropPage.ApplyCropCommand = new AsyncRelayCommand(HandleApplyCrop);
        CropPage.ResetCropCommand = new AsyncRelayCommand(HandleResetCrop);
        BalancePage.Metrics.TargetsSaved = HandleBalanceTargetsSaved;

        SpringPage.TimeZoom = _timeZoom;
        DamperPage.TimeZoom = _timeZoom;
        MiscPage.TimeZoom = _timeZoom;
        _timeZoom.WindowChanged += OnZoomWindowChanged;
    }

    public SessionViewModel(Session session, bool fromDatabase)
    {
        this.session = session;
        IsInDatabase = fromDatabase;
        Pages = [SummaryPage, SpringPage, DamperPage, BalancePage, MiscPage, NotesPage];
        SummaryPage.ChangeSetupCommand = new AsyncRelayCommand(HandleSetupReassign);
        ShareBalanceMetricsWithSummary();
        CropPage.ApplyCropCommand = new AsyncRelayCommand(HandleApplyCrop);
        CropPage.ResetCropCommand = new AsyncRelayCommand(HandleResetCrop);
        BalancePage.Metrics.TargetsSaved = HandleBalanceTargetsSaved;

        SpringPage.TimeZoom = _timeZoom;
        DamperPage.TimeZoom = _timeZoom;
        MiscPage.TimeZoom = _timeZoom;
        _timeZoom.WindowChanged += OnZoomWindowChanged;

        NotesPage.ForkSettings.PropertyChanged += (_, _) => EvaluateDirtiness();
        NotesPage.ShockSettings.PropertyChanged += (_, _) => EvaluateDirtiness();
        NotesPage.PropertyChanged += (_, _) => EvaluateDirtiness();
        CropPage.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(CropPage.IsModified)) OnPropertyChanged(nameof(SaveLabel)); };

        // Persist pending changes to the DB the moment the user toggles the pencil —
        // they need to survive a session import even if the user never explicitly saves.
        NotesPage.PersistPendingAsync = PersistPendingAsync;

        // Other VMs (or the import flow) can update this setup's pending row; reload
        // ours so a stale list doesn't linger after the row is cleared on import.
        PendingSetupChanges.Changed += OnPendingSetupChangesChanged;

        _ = ResetImplementation();
    }

    private async Task PersistPendingAsync()
    {
        if (session.Setup is not Guid setupId) return;
        var dbSvc = App.Current?.Services?.GetService<IDatabaseService>();
        if (dbSvc is null) return;

        if (NotesPage.PendingChanges.Count > 0)
            await dbSvc.PutPendingSetupChangesAsync(NotesPage.BuildPending(setupId));
        else
            await dbSvc.DeletePendingSetupChangesAsync(setupId);
    }

    private async void OnPendingSetupChangesChanged(object? sender, Guid setupId)
    {
        if (session.Setup != setupId) return;
        var dbSvc = App.Current?.Services?.GetService<IDatabaseService>();
        if (dbSvc is null) return;
        var pending = await dbSvc.GetPendingSetupChangesAsync(setupId);
        Dispatcher.UIThread.Post(() => NotesPage.LoadPending(pending));
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
                setup: session.Setup,
                timestamp: session.Timestamp,
                track: session.Track)
            {
                FrontSpringRate = NotesPage.ForkSettings.SpringRate,
                FrontVolSpc = NotesPage.ForkSettings.VolSpc,
                FrontHighSpeedCompression = NotesPage.ForkSettings.HighSpeedCompression,
                FrontLowSpeedCompression = NotesPage.ForkSettings.LowSpeedCompression,
                FrontLowSpeedRebound = NotesPage.ForkSettings.LowSpeedRebound,
                FrontHighSpeedRebound = NotesPage.ForkSettings.HighSpeedRebound,
                FrontTirePressure = NotesPage.ForkSettings.TirePressure,
                RearSpringRate = NotesPage.ShockSettings.SpringRate,
                RearVolSpc = NotesPage.ShockSettings.VolSpc,
                RearHighSpeedCompression = NotesPage.ShockSettings.HighSpeedCompression,
                RearLowSpeedCompression = NotesPage.ShockSettings.LowSpeedCompression,
                RearLowSpeedRebound = NotesPage.ShockSettings.LowSpeedRebound,
                RearHighSpeedRebound = NotesPage.ShockSettings.HighSpeedRebound,
                RearTirePressure = NotesPage.ShockSettings.TirePressure,
                HasProcessedData = IsComplete,
                CropStartSample = session.CropStartSample,
                CropEndSample   = session.CropEndSample,
            };

            await databaseService.PutSessionAsync(newSession);

            if (newSession.Setup is { } setupId)
            {
                if (NotesPage.PendingChanges.Count > 0)
                {
                    await databaseService.PutPendingSetupChangesAsync(NotesPage.BuildPending(setupId));
                }
                else
                {
                    await databaseService.DeletePendingSetupChangesAsync(setupId);
                }
            }

            session = newSession;
            IsDirty = false;
            IsInDatabase = true;
        }
        catch (Exception e)
        {
            ErrorMessages.Add($"Session could not be saved: {e.Message}");
        }
    }

    protected override async Task ResetImplementation()
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
        NotesPage.ForkSettings.TirePressure = session.FrontTirePressure;

        NotesPage.ShockSettings.SpringRate = session.RearSpringRate;
        NotesPage.ShockSettings.VolSpc = session.RearVolSpc;
        NotesPage.ShockSettings.HighSpeedCompression = session.RearHighSpeedCompression;
        NotesPage.ShockSettings.LowSpeedCompression = session.RearLowSpeedCompression;
        NotesPage.ShockSettings.LowSpeedRebound = session.RearLowSpeedRebound;
        NotesPage.ShockSettings.HighSpeedRebound = session.RearHighSpeedRebound;
        NotesPage.ShockSettings.TirePressure = session.RearTirePressure;

        Timestamp = DateTimeOffset.FromUnixTimeSeconds(session.Timestamp ?? 0).LocalDateTime;

        if (session.Setup is { } setupId)
        {
            var databaseService = App.Current?.Services?.GetService<IDatabaseService>();
            if (databaseService != null)
            {
                var pending = await databaseService.GetPendingSetupChangesAsync(setupId);
                NotesPage.LoadPending(pending);
            }
        }
    }

    #endregion

    #region Commands

    // Called after import to pre-generate the plot cache in the background,
    // before the user opens the session. Uses the last known bounds (updated on each Loaded call).
    // A freshly imported session can hand over its in-memory TelemetryData via `preloaded`,
    // skipping the DB blob read + full deserialize (fresh imports are never cropped and always
    // carry the current ProcessingVersion, so the GetSessionPsstAsync migration path is moot).
    internal async Task PrecomputeCache(TelemetryData? preloaded = null)
    {
        try
        {
            var swTotal = Stopwatch.StartNew();
            if (!IsComplete) return;
            var databaseService = App.Current?.Services?.GetService<IDatabaseService>();
            if (databaseService is null) return;

            var cacheExists = await databaseService.GetSessionCacheAsync(Id) is not null;
            if (cacheExists) return;

            var telemetryData = preloaded;
            if (telemetryData is null)
            {
                var swLoad = Stopwatch.StartNew();
                telemetryData = await databaseService.GetSessionPsstAsync(Id);
                PerfLog.Log("cache/loadPsst", swLoad.Elapsed.TotalMilliseconds);
            }
            if (telemetryData is null) return;

            if (session.CropStartSample.HasValue && session.CropEndSample.HasValue)
            {
                var swCrop = Stopwatch.StartNew();
                var cropped = telemetryData.CreateCroppedCopy(session.CropStartSample.Value, session.CropEndSample.Value);
                PerfLog.Log("cache/crop", swCrop.Elapsed.TotalMilliseconds);
                await CreateCache(LastKnownBounds, cropped, telemetryData);
            }
            else
            {
                await CreateCache(LastKnownBounds, telemetryData);
            }
            PerfLog.Log($"cache/precompute {Id}", swTotal.Elapsed.TotalMilliseconds);
        }
        catch
        {
            // Best-effort — user opening the session will retry
        }
    }

    private async Task HandleApplyCrop()
    {
        var databaseService = App.Current?.Services?.GetService<IDatabaseService>();
        Debug.Assert(databaseService != null, nameof(databaseService) + " != null");

        var start = CropPage.CropStartSample;
        var end   = CropPage.CropEndSample;

        // Minimum crop length guard
        if (end - start < 100)
        {
            ErrorMessages.Add("Crop region too short (minimum 100 samples).");
            return;
        }

        // Skip reanalysis if crop is unchanged — treat "full range" as equivalent to "no crop"
        var existingStart = session.CropStartSample ?? 0;
        var existingEnd   = session.CropEndSample   ?? CropPage.TotalSamples;
        if (start == existingStart && end == existingEnd)
        {
            IsCropVisible = false;
            return;
        }

        try
        {
            IsAnalyzingData = true;

            session.CropStartSample = start;
            session.CropEndSample   = end;
            await databaseService.PutSessionAsync(session);

            var fullData = await databaseService.GetSessionPsstAsync(Id);
            if (fullData is null) throw new Exception("Session data not found.");

            CropPage.FullData   = fullData;
            CropPage.ViewBounds = LastKnownBounds;

            var cropped = fullData.CreateCroppedCopy(start, end);
            await CreateCache(LastKnownBounds, cropped, fullData);
            CropPage.OriginalStartSample = start;
            CropPage.OriginalEndSample   = end;

            // New crop → new analysis data: rebuild zoom state + mini-map and reset the window.
            _analysisData = null;
            InitializeTimeZoom();
            IsCropVisible = false;
        }
        catch (Exception e)
        {
            ErrorMessages.Add($"Crop failed: {e.Message}");
        }
        finally
        {
            IsAnalyzingData = false;
        }
    }

    // Confirmed balance-target edits are stored per discipline; the FrontSag/RearSag green
    // ranges feed the expected pitch band baked into the cached PitchBalance SVG. When an edit
    // moves that band, rebuild this session's cache immediately (same flow as HandleApplyCrop)
    // so the displayed plot doesn't contradict the freshly re-colored μ row. Every other
    // session of the discipline heals via the band-signature check in LoadCache on next open.
    private async Task HandleBalanceTargetsSaved()
    {
        var databaseService = App.Current?.Services?.GetService<IDatabaseService>();
        if (databaseService is null) return;

        try
        {
            var cache = await databaseService.GetSessionCacheAsync(Id);
            if (cache?.PitchBalance is null || cache.BalanceMetricsJson is null) return;
            var metrics = JsonSerializer.Deserialize<BalanceMetrics>(cache.BalanceMetricsJson);
            if (metrics is null) return;
            if (PitchBandMatchesCache(await ComputeExpectedPitchBandAsync(metrics), cache)) return;

            IsAnalyzingData = true;
            try
            {
                var fullData = await databaseService.GetSessionPsstAsync(Id);
                if (fullData is null) return;
                if (session.CropStartSample.HasValue && session.CropEndSample.HasValue)
                    await CreateCache(LastKnownBounds, fullData.CreateCroppedCopy(
                        session.CropStartSample.Value, session.CropEndSample.Value), fullData);
                else
                    await CreateCache(LastKnownBounds, fullData);
            }
            finally
            {
                IsAnalyzingData = false;
            }
        }
        catch (Exception e)
        {
            ErrorMessages.Add($"Could not refresh plots after target edit: {e.Message}");
        }
    }

    private async Task HandleResetCrop()
    {
        var databaseService = App.Current?.Services?.GetService<IDatabaseService>();
        Debug.Assert(databaseService != null, nameof(databaseService) + " != null");

        try
        {
            IsAnalyzingData = true;

            session.CropStartSample = null;
            session.CropEndSample   = null;
            await databaseService.PutSessionAsync(session);

            // Reset UI sliders to full range
            CropPage.CropStartSample = 0;
            CropPage.CropEndSample   = CropPage.TotalSamples;

            var fullData = await databaseService.GetSessionPsstAsync(Id);
            if (fullData is null) throw new Exception("Session data not found.");

            CropPage.FullData   = fullData;
            CropPage.ViewBounds = LastKnownBounds;

            await CreateCache(LastKnownBounds, fullData);
            CropPage.OriginalStartSample = 0;
            CropPage.OriginalEndSample   = CropPage.TotalSamples;

            // Crop cleared → analysis data is the full session again: rebuild zoom state + mini-map.
            _analysisData = null;
            InitializeTimeZoom();
            IsCropVisible = false;
        }
        catch (Exception e)
        {
            ErrorMessages.Add($"Reset crop failed: {e.Message}");
        }
        finally
        {
            IsAnalyzingData = false;
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
            SummaryPage.IsEditingSetup = false;
            var telemetryData = await databaseService.GetSessionPsstAsync(Id);
            if (telemetryData != null)
            {
                if (session.CropStartSample.HasValue && session.CropEndSample.HasValue)
                {
                    var cropped = telemetryData.CreateCroppedCopy(session.CropStartSample.Value, session.CropEndSample.Value);
                    await CreateCache(LastKnownBounds, cropped, telemetryData);
                }
                else
                {
                    await CreateCache(LastKnownBounds, telemetryData);
                }
            }
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
                var fullData = await databaseService.GetSessionPsstAsync(Id);

                if (needsRecreate)
                {
                    if (fullData is null)
                    {
                        throw new Exception("Database error");
                    }

                    // Initialize CropPage slider state from session boundaries
                    var totalSamples = fullData.Front.Present
                        ? fullData.Front.Travel.Length
                        : fullData.Rear.Present ? fullData.Rear.Travel.Length : 0;
                    CropPage.SampleRate    = fullData.SampleRate;
                    CropPage.TotalSamples  = totalSamples;
                    CropPage.OriginalStartSample = session.CropStartSample ?? 0;
                    CropPage.OriginalEndSample   = session.CropEndSample   ?? totalSamples;
                    CropPage.CropStartSample = CropPage.OriginalStartSample;
                    CropPage.CropEndSample   = CropPage.OriginalEndSample;
                    CropPage.FullData    = fullData;
                    CropPage.ViewBounds  = bounds;

                    // If crop boundaries are set, analyze the cropped slice; TravelTimeHistory always uses full data
                    TelemetryData analyzeData;
                    if (session.CropStartSample.HasValue && session.CropEndSample.HasValue)
                        analyzeData = fullData.CreateCroppedCopy(session.CropStartSample.Value, session.CropEndSample.Value);
                    else
                        analyzeData = fullData;

                    // CreateCache also populates summary and persists both
                    IsAnalyzingData = true;
                    try { await CreateCache(bounds, analyzeData, fullData); }
                    finally { IsAnalyzingData = false; }
                }
                else if (fullData is not null && needsSummary)
                {
                    // Cache was valid but summary was missing (old cache without summary_json)
                    var summaryData = await PopulateSummary(fullData);

                    var cache = await databaseService.GetSessionCacheAsync(Id);
                    if (cache is not null)
                    {
                        cache.SummaryJson = JsonSerializer.Serialize(summaryData);
                        await databaseService.PutSessionCacheAsync(cache);
                    }
                }
            }

            // Initialize CropPage slider state from cache (when cache was valid, fullData not loaded)
            if (!needsRecreate && CropPage.TotalSamples == 0)
            {
                var cachedData = await databaseService.GetSessionPsstAsync(Id);
                if (cachedData is not null)
                {
                    var totalSamples = cachedData.Front.Present
                        ? cachedData.Front.Travel.Length
                        : cachedData.Rear.Present ? cachedData.Rear.Travel.Length : 0;
                    CropPage.SampleRate      = cachedData.SampleRate;
                    CropPage.TotalSamples    = totalSamples;
                    CropPage.OriginalStartSample = session.CropStartSample ?? 0;
                    CropPage.OriginalEndSample   = session.CropEndSample   ?? totalSamples;
                    CropPage.CropStartSample = CropPage.OriginalStartSample;
                    CropPage.CropEndSample   = CropPage.OriginalEndSample;
                    CropPage.FullData    = cachedData;
                    CropPage.ViewBounds  = LastKnownBounds;
                }
            }

            // Data (CropPage.FullData) is now populated — (re)initialise the shared time-zoom state
            // and context mini-map. Idempotent: no-ops once initialised for the current data/crop.
            InitializeTimeZoomIfNeeded();
        }
        catch (Exception e)
        {
            ErrorMessages.Add($"Could not load session data: {e.Message}");
        }
    }

    protected override bool CanExportPdf() => IsComplete;

    protected override Task ExportPdf() => ExportPdfCore(essential: false);

    // Distinct name (rather than nameof(CanExportPdf) again) sidesteps MVVMTK0010: the source
    // generator treats the inherited virtual and this type's override of CanExportPdf as two
    // separate matches for a nameof() lookup within this class.
    private bool CanExportPdfEssential() => CanExportPdf();

    // Reduced customer-facing report: Spring/Damper/Balance highlights only, no FFTs,
    // pitch/G-out diagnostics, phase-portrait plots, or Misc time-series pages.
    [RelayCommand(CanExecute = nameof(CanExportPdfEssential))]
    private Task ExportPdfEssential() => ExportPdfCore(essential: true);

    private async Task ExportPdfCore(bool essential)
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

            // Combined sessions don't cache the phase-portrait plots (CreateCache skips them,
            // see the isCombined gate there) — but the full report must still include the
            // fork/damper position-vs-velocity pages, so render them fresh from telemetry on
            // demand here. The essential report never includes these pages, so skip this
            // (potentially expensive) regeneration entirely for that variant.
            string? frontPosVelSvg = null;
            string? rearPosVelSvg = null;
            if (!essential)
            {
                frontPosVelSvg = cache.FrontPositionVelocity;
                rearPosVelSvg = cache.RearPositionVelocity;
                if (frontPosVelSvg is null || rearPosVelSvg is null)
                {
                    var pdfTelemetryData = await databaseService.GetSessionPsstAsync(Id);
                    if (pdfTelemetryData is not null)
                    {
                        if (session.CropStartSample.HasValue && session.CropEndSample.HasValue)
                            pdfTelemetryData = pdfTelemetryData.CreateCroppedCopy(
                                session.CropStartSample.Value, session.CropEndSample.Value);

                        var (pvWidth, pvHeight) = ((int)LastKnownBounds.Width, (int)(LastKnownBounds.Height / 2.0));

                        if (frontPosVelSvg is null && pdfTelemetryData.Front.Present)
                        {
                            var fpv = new PositionVelocityPlot(new Plot(), SuspensionType.Front);
                            fpv.LoadTelemetryData(pdfTelemetryData);
                            frontPosVelSvg = fpv.Plot.GetSvgXml(pvWidth, pvHeight);
                        }
                        if (rearPosVelSvg is null && pdfTelemetryData.Rear.Present)
                        {
                            var rpv = new PositionVelocityPlot(new Plot(), SuspensionType.Rear);
                            rpv.LoadTelemetryData(pdfTelemetryData);
                            rearPosVelSvg = rpv.Plot.GetSvgXml(pvWidth, pvHeight);
                        }
                    }
                }
            }

            // Collect all SVG entries in tab display order, each tagged with whether it's part
            // of the reduced "essential" (customer) report. Entries for the low-speed velocity
            // histograms also carry the zone-percentage band data so RenderSvgsToPdf can render
            // the VelocityBandView equivalent below the plot on the same page.
            var svgEntries = new List<(PdfSvgEntry? Entry, bool Essential)>
            {
                // Spring tab
                (PdfSvgEntry.For(cache.TravelComparisonHistogram), true),
                (PdfSvgEntry.For(cache.FrontRearTravelScatter), true),
                (PdfSvgEntry.For(cache.FrontTravelHistogram), true),
                (PdfSvgEntry.For(cache.RearTravelHistogram), true),

                // Damper tab
                (PdfSvgEntry.For(cache.VelocityDistributionComparison), true),
                (PdfSvgEntry.For(cache.FrontVelocityHistogram), true),
                (PdfSvgEntry.For(cache.FrontLowSpeedVelocityHistogram, "Front Zone %",
                    cache.FrontHsrPercentage, cache.FrontLsrPercentage, cache.FrontLscPercentage, cache.FrontHscPercentage), true),
                (PdfSvgEntry.For(cache.RearVelocityHistogram), true),
                (PdfSvgEntry.For(cache.RearLowSpeedVelocityHistogram, "Rear Zone %",
                    cache.RearHsrPercentage, cache.RearLsrPercentage, cache.RearLscPercentage, cache.RearHscPercentage), true),
                (PdfSvgEntry.For(cache.RearDamperVelocityHistogram), false),

                // Balance tab
                (PdfSvgEntry.For(cache.CombinedTravelFft), false),
                (PdfSvgEntry.For(cache.CombinedTravelFftHigh), false),
                (PdfSvgEntry.For(cache.CombinedVelocityFft), false),
                (PdfSvgEntry.For(cache.CombinedBalance), true),
                (PdfSvgEntry.For(cache.CompressionBalance), true),
                (PdfSvgEntry.For(cache.ReboundBalance), true),
                (PdfSvgEntry.For(cache.PitchBalance), false),
                (PdfSvgEntry.For(cache.PitchCoherence), false),
                (PdfSvgEntry.For(cache.GoutScatter), false),
                (PdfSvgEntry.For(cache.CumulativeTravel), false),
                (PdfSvgEntry.For(frontPosVelSvg), false),
                (PdfSvgEntry.For(rearPosVelSvg), false),

                // Misc tab (time-series: travel -> velocity -> acceleration, front/rear)
                (PdfSvgEntry.For(cache.FrontTravelTimeCropped), false),
                (PdfSvgEntry.For(cache.RearTravelTimeCropped), false),
                (PdfSvgEntry.For(cache.FrontVelocityTimeCropped), false),
                (PdfSvgEntry.For(cache.RearVelocityTimeCropped), false),
                (PdfSvgEntry.For(cache.FrontAccelerationTimeCropped), false),
                (PdfSvgEntry.For(cache.RearAccelerationTimeCropped), false),
            };

            var validSvgs = svgEntries
                .Where(s => s.Entry is not null && (!essential || s.Essential))
                .Select(s => s.Entry!)
                .ToList();
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
            ("Tire pres.", notes.ForkSettings.TirePressure?.ToString("F1"),  notes.ShockSettings.TirePressure?.ToString("F1")),
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

    // One PDF page's worth of content: an SVG plot, plus (optionally) the zone-percentage
    // band data that mirrors VelocityBandView, rendered directly below the plot on export.
    private sealed record PdfSvgEntry(string Svg, VelocityBandData? Band)
    {
        public static PdfSvgEntry? For(string? svg) => svg is null ? null : new PdfSvgEntry(svg, null);

        public static PdfSvgEntry? For(string? svg, string bandTitle,
            double? hsr, double? lsr, double? lsc, double? hsc)
        {
            if (svg is null) return null;
            var band = VelocityBandData.Create(bandTitle, hsr, lsr, lsc, hsc);
            return new PdfSvgEntry(svg, band);
        }
    }

    // Mirrors VelocityBandView's HSR/LSR/LSC/HSC percentages. Only constructed when all four
    // percentages are present, matching the app control's IsVisible-on-non-null pattern.
    private sealed record VelocityBandData(string Title, double Hsr, double Lsr, double Lsc, double Hsc)
    {
        public static VelocityBandData? Create(string title, double? hsr, double? lsr, double? lsc, double? hsc)
        {
            if (hsr is null || lsr is null || lsc is null || hsc is null) return null;
            return new VelocityBandData(title, hsr.Value, lsr.Value, lsc.Value, hsc.Value);
        }
    }

    // Rendered height of the zone-band block appended below a low-speed velocity histogram
    // page, mirroring VelocityBandView's layout (title text + 44px band grid + spacing).
    private const float PdfBandBlockHeight = 62f;
    private const float PdfBandLeftInset = 50f;   // Mirrors VelocityBandView's Margin="50,0,20,0"
    private const float PdfBandRightInset = 20f;
    private const float PdfBandGridHeight = 44f;

    private string RenderSvgsToPdf(List<PdfSvgEntry> svgEntries, SummaryPageViewModel summary, NotesPageViewModel notes)
    {
        var tempDir = System.IO.Path.GetTempPath();
        // Strip characters that are invalid in filenames or URLs (space, #, %, &, etc.)
        var sanitizedName = System.Text.RegularExpressions.Regex.Replace(Name ?? "session", @"[^\w\-.]", "_");
        var pdfPath = System.IO.Path.Combine(tempDir, $"{sanitizedName}.pdf");

        // Parse all SVGs in parallel (expensive XML + Skia picture recording),
        // then write PDF pages sequentially (SKDocument is not thread-safe).
        var pages = svgEntries
            .AsParallel()
            .AsOrdered()
            .Select(entry =>
            {
                var svg = new Svg.Skia.SKSvg();
                svg.FromSvg(entry.Svg);
                return (Svg: svg, entry.Band);
            })
            .ToList();

        try
        {
            using var stream = new System.IO.FileStream(pdfPath, System.IO.FileMode.Create);
            using var document = SkiaSharp.SKDocument.CreatePdf(stream);

            DrawSummaryPage(document, summary, (float)LastKnownBounds.Width);

            foreach (var (svg, band) in pages)
            {
                var picture = svg.Picture;
                if (picture is null) continue;

                var bounds = picture.CullRect;
                var pageHeight = bounds.Height + (band is not null ? PdfBandBlockHeight : 0f);
                using var canvas = document.BeginPage(bounds.Width, pageHeight);
                canvas.DrawPicture(picture);
                if (band is not null)
                    DrawVelocityBand(canvas, band, bounds.Width, bounds.Height, pageHeight);
                document.EndPage();
            }

            DrawNotesPage(document, notes, (float)LastKnownBounds.Width);

            document.Close();
        }
        finally
        {
            foreach (var (svg, _) in pages)
                svg.Dispose();
        }

        return pdfPath;
    }

    // Draws the zone-percentage band block below a low-speed velocity histogram, mirroring
    // VelocityBandView.axaml: bold title, then four columns (HSR/LSR/LSC/HSC) sized
    // proportionally to their percentage, each with a border and a "LABEL / value" text.
    private static void DrawVelocityBand(SkiaSharp.SKCanvas canvas, VelocityBandData band,
        float pageWidth, float top, float pageHeight)
    {
        var titleColor = SkiaSharp.SKColor.Parse("#d0d0d0");
        var borderColor = SkiaSharp.SKColor.Parse("#505558");
        var outerBg = SkiaSharp.SKColor.Parse("#303030");
        var innerBg = SkiaSharp.SKColor.Parse("#282828");
        var textColor = SkiaSharp.SKColor.Parse("#d0d0d0");

        using var titlePaint = new SkiaSharp.SKPaint
        {
            IsAntialias = true, TextSize = 12f, Color = titleColor,
            Typeface = SkiaSharp.SKTypeface.FromFamilyName(null, SkiaSharp.SKFontStyle.Bold),
        };
        using var labelPaint = new SkiaSharp.SKPaint
        {
            IsAntialias = true, TextSize = 11f, Color = textColor, TextAlign = SkiaSharp.SKTextAlign.Center,
            Typeface = SkiaSharp.SKTypeface.FromFamilyName(null, SkiaSharp.SKFontStyle.Bold),
        };
        using var fillPaint = new SkiaSharp.SKPaint { IsStroke = false };
        using var strokePaint = new SkiaSharp.SKPaint { IsStroke = true, StrokeWidth = 1f, Color = borderColor };

        // The SVG plot only covers its own CullRect; paint the appended band strip with the
        // plots' figure background (#15191c) so the page reads as one continuous dark panel.
        fillPaint.Color = SkiaSharp.SKColor.Parse("#15191c");
        canvas.DrawRect(new SkiaSharp.SKRect(0f, top, pageWidth, pageHeight), fillPaint);

        float left = PdfBandLeftInset;
        float right = pageWidth - PdfBandRightInset;
        float gridWidth = right - left;

        // Title, left-aligned above the grid (mirrors Margin="0,4,0,0" + Margin="0,0,0,2")
        float titleY = top + 4f + 12f;
        canvas.DrawText(band.Title, left, titleY, titlePaint);

        float gridTop = titleY + 2f;
        float gridBottom = System.Math.Min(gridTop + PdfBandGridHeight, pageHeight);
        float gridHeight = gridBottom - gridTop;

        var segments = new (string Label, double Value, SkiaSharp.SKColor Bg)[]
        {
            ("HSR", band.Hsr, outerBg),
            ("LSR", band.Lsr, innerBg),
            ("LSC", band.Lsc, innerBg),
            ("HSC", band.Hsc, outerBg),
        };

        double total = segments.Sum(s => s.Value);
        if (total <= 0) total = 1; // guard against div-by-zero; degenerates to equal widths

        float x = left;
        for (int i = 0; i < segments.Length; i++)
        {
            var (label, value, bg) = segments[i];
            float w = (float)(gridWidth * (value / total));
            // Absorb rounding error into the last column so borders line up with `right`.
            if (i == segments.Length - 1) w = right - x;

            var rect = new SkiaSharp.SKRect(x, gridTop, x + w, gridBottom);
            fillPaint.Color = bg;
            canvas.DrawRect(rect, fillPaint);
            canvas.DrawRect(rect, strokePaint);

            float cx = x + w / 2f;
            float labelY = gridTop + gridHeight / 2f - 2f;
            float valueY = labelY + 13f;
            canvas.DrawText(label, cx, labelY, labelPaint);
            canvas.DrawText(value.ToString("0.0"), cx, valueY, labelPaint);

            x += w;
        }
    }

    #endregion
}
