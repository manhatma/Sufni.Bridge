using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Svg.Skia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using DynamicData;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using ScottPlot;
using Sufni.Bridge.Models;
using Sufni.Bridge.Models.Telemetry;
using Sufni.Bridge.Plots;
using Sufni.Bridge.Services;
using Sufni.Bridge.Extensions;
using Sufni.Bridge.ViewModels;
using Sufni.Bridge.ViewModels.SessionPages;
using static Sufni.Bridge.Extensions.SvgHelpers;

namespace Sufni.Bridge.ViewModels.Items;

public partial class SessionViewModel : ItemViewModelBase
{
    // Increment when plot visuals change to force cache regeneration on all sessions.
    internal const int CurrentPlotVersion = 234;

    // Approximate rendered height of the VelocityBandView control (margin + title text +
    // 44 px band grid). Used to size the low-speed velocity histograms so the
    // histogram+bands pair matches a full normal histogram.
    internal const int VelocityBandViewHeight = 70;

    // Limits concurrent plot generation tasks to reduce peak memory on iOS.
    private static readonly SemaphoreSlim s_plotSemaphore = new(3, 3);

    // Shared across all instances — updated whenever any session loads with real bounds.
    // Default matches iPhone 15 Pro logical width; height/2 is used for plots.
    internal static Rect LastKnownBounds = new Rect(0, 0, 393, 700);

    private Session session;
    internal Session SessionModel => session;
    public bool IsInDatabase;
    internal SpringPageViewModel SpringPage { get; } = new();
    internal DamperPageViewModel DamperPage { get; } = new();
    internal BalancePageViewModel BalancePage { get; } = new();
    internal MiscPageViewModel MiscPage { get; } = new();
    internal SummaryPageViewModel SummaryPage { get; } = new();

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

    // Read-path state. _pagesPopulated: pages hold a complete render of the current cache row
    // (valid load or rebuild), so a later Loaded() only re-validates the scalar cache meta and
    // skips the wide row fetch + SVG re-parse. _backgroundSvgParse: the deferred parse of the
    // non-Spring SVGs, awaited by the full-data load below so the blob deserialize doesn't
    // compete with the parse burst. _fullDataLoad: in-flight deferred telemetry-blob load.
    private bool _pagesPopulated;
    private Task _backgroundSvgParse = Task.CompletedTask;
    private Task? _fullDataLoad;

    internal NotesPageViewModel NotesPage { get; } = new();
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

    partial void OnIsCombinedSessionChanged(bool value)
    {
        NotesPage.IsCombinedSession = value;
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
                var swMini = Stopwatch.StartNew();
                // One overview strip per domain (travel / velocity / acceleration), each with prominent
                // airtime bands for navigation. The TimeZoomControl on each page shows the matching one.
                var travelSrc = SvgToSource(RenderOverviewXml(new TravelTimeHistoryPlot(new Plot(), showAirtimeBands: true), data, width, miniHeight));
                var velocitySrc = SvgToSource(RenderOverviewXml(new VelocityTimeHistoryPlot(new Plot(), showAirtimeBands: true), data, width, miniHeight));
                var accelSrc = SvgToSource(RenderOverviewXml(new AccelerationTimeHistoryPlot(new Plot(), showAirtimeBands: true), data, width, miniHeight));
                PerfLog.Log("load/miniMap", swMini.Elapsed.TotalMilliseconds);
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
        Dispatcher.UIThread.Post(() =>
        {
            if (_timeZoomSnapshotTaken)
            {
                SpringPage.FrontTravelTimeCropped     = _fullFrontTravel;
                SpringPage.RearTravelTimeCropped      = _fullRearTravel;
                DamperPage.FrontVelocityTimeCropped   = _fullFrontVelocity;
                DamperPage.RearVelocityTimeCropped    = _fullRearVelocity;
                MiscPage.FrontAccelerationTimeCropped = _fullFrontAccel;
                MiscPage.RearAccelerationTimeCropped  = _fullRearAccel;
            }
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

    // Staleness decision for a cache row, on scalars only: row exists, current PlotVersion,
    // crop bounds match the session, and the pitch-band signature still matches the band
    // implied by the CURRENT per-discipline overrides — the μ row re-colors live from those
    // overrides, so a stale band would contradict it in the same view.
    private async Task<bool> IsCacheMetaCurrentAsync(SessionCacheMeta? meta)
    {
        if (meta is null || meta.PlotVersion != CurrentPlotVersion)
        {
            return false;
        }

        if (meta.CropStartSample != session.CropStartSample ||
            meta.CropEndSample   != session.CropEndSample)
        {
            return false;
        }

        if (meta.HasPitchBalance && meta.BalanceMetricsJson is not null)
        {
            try
            {
                var m = JsonSerializer.Deserialize<BalanceMetrics>(meta.BalanceMetricsJson);
                if (m is not null && !PitchBandMatches(await ComputeExpectedPitchBandAsync(m),
                        meta.PitchExpectedMinDeg, meta.PitchExpectedMaxDeg))
                {
                    return false;
                }
            }
            catch
            {
                // Corrupt metrics cache — the completeness checks in LoadCache trigger a rebuild
            }
        }

        return true;
    }

    // Returns (cacheFound, hasVdc, hasPvc) so the caller can detect incomplete old caches
    // without checking in-memory properties that lazy loading hasn't set yet. `meta` is the
    // scalar projection of the cache row, already fetched by the caller — the wide row (~30
    // SVG columns, often tens of MB) is only materialized after meta passed the staleness
    // checks, so a stale cache (e.g. after a PlotVersion bump) never pays the full fetch.
    private Task<(bool found, bool hasVdc, bool hasPvc)> LoadCache(SessionCacheMeta? meta) =>
        SessionCacheLoader.LoadAsync(
            this,
            meta,
            IsCacheMetaCurrentAsync,
            GetSessionDisciplineAsync,
            GetBalanceOverridesAsync,
            task => _backgroundSvgParse = task);

    // Kicks off the deferred load of the full telemetry blob (crop slider fallback, zoom
    // mini-map, windowed re-renders). With a valid cache the open path doesn't need the blob —
    // slider bounds come from the cache meta — so it's deserialized in the background AFTER
    // the SVG parse burst instead of competing with it for cores. No-op while a load is in
    // flight or once the data is there; a failed attempt is retried on the next Loaded.
    private void EnsureFullDataLoaded(IDatabaseService databaseService)
    {
        if (CropPage.FullData is not null) return;
        if (_fullDataLoad is { IsCompleted: false }) return;
        _fullDataLoad = LoadFullDataAsync(databaseService);
    }

    // Must be started from the UI thread (continuations mutate CropPage / zoom state).
    private async Task LoadFullDataAsync(IDatabaseService databaseService)
    {
        try
        {
            await _backgroundSvgParse;

            var sw = Stopwatch.StartNew();
            // Task.Run: the MessagePack deserialize of a multi-MB blob runs as a continuation
            // inside GetSessionPsstAsync and must not land on the UI thread.
            var fullData = await Task.Run(() => databaseService.GetSessionPsstAsync(Id));
            PerfLog.Log("load/fullData", sw.Elapsed.TotalMilliseconds);
            if (fullData is null) return;

            var totalSamples = fullData.Front.Present
                ? fullData.Front.Travel.Length
                : fullData.Rear.Present ? fullData.Rear.Travel.Length : 0;
            if (CropPage.TotalSamples == 0)
            {
                // Cache row predates the sample_rate/sample_count columns — seed the crop
                // slider state from the blob, exactly like the old eager path did.
                CropPage.SampleRate    = fullData.SampleRate;
                CropPage.TotalSamples  = totalSamples;
                CropPage.OriginalStartSample = session.CropStartSample ?? 0;
                CropPage.OriginalEndSample   = session.CropEndSample   ?? totalSamples;
                CropPage.CropStartSample = CropPage.OriginalStartSample;
                CropPage.CropEndSample   = CropPage.OriginalEndSample;
            }
            CropPage.FullData   = fullData;
            CropPage.ViewBounds = LastKnownBounds;

            // Zoom state waited for this data — no-op if already initialised (e.g. crop apply).
            InitializeTimeZoomIfNeeded();
        }
        catch
        {
            // Best-effort: the cached plots are unaffected; crop/zoom stay disabled until retry.
        }
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
            BalanceTargetDefaults.EffectiveRearSagBand(overrides, discipline,
                m.MaxRearStrokeMm, m.ShockWheelCoeffs, m.MaxRearTravelMm),
            m.MaxFrontTravelMm, m.MaxRearTravelMm, m.WheelbaseMm);
    }

    private static bool PitchBandMatches((double minDeg, double maxDeg)? band,
        double? cachedMinDeg, double? cachedMaxDeg)
    {
        static bool Eq(double? a, double? b) =>
            (a is null && b is null) || (a.HasValue && b.HasValue && Math.Abs(a.Value - b.Value) < 1e-9);
        return Eq(band?.minDeg, cachedMinDeg) && Eq(band?.maxDeg, cachedMaxDeg);
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

        await SessionCacheBuilder.BuildAsync(
            this,
            bounds,
            telemetryData,
            fullData,
            session,
            databaseService,
            swCache,
            GetSessionDisciplineAsync,
            GetBalanceOverridesAsync,
            (data, frontBandsTask, rearBandsTask) =>
                SessionSummaryBuilder.PopulateSummary(this, data, frontBandsTask, rearBandsTask),
            ThrottledPlotTask);
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
        CropPage.SaveCropAsCopyCommand = new AsyncRelayCommand(HandleSaveCropAsCopy);
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
        CropPage.SaveCropAsCopyCommand = new AsyncRelayCommand(HandleSaveCropAsCopy);
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
        PopulateNotesSetup();

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

    private void PopulateNotesSetup()
    {
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

        NotesPage.ForkSettings.SpringRateDisplay = SessionSetupValues.Get(this, model => model.FrontSpringRate);
        NotesPage.ForkSettings.VolSpcDisplay = SessionSetupValues.Get(this, model => FormatSetupDouble(model.FrontVolSpc));
        NotesPage.ForkSettings.HighSpeedCompressionDisplay = SessionSetupValues.Get(this, model => FormatSetupInt(model.FrontHighSpeedCompression));
        NotesPage.ForkSettings.LowSpeedCompressionDisplay = SessionSetupValues.Get(this, model => FormatSetupInt(model.FrontLowSpeedCompression));
        NotesPage.ForkSettings.LowSpeedReboundDisplay = SessionSetupValues.Get(this, model => FormatSetupInt(model.FrontLowSpeedRebound));
        NotesPage.ForkSettings.HighSpeedReboundDisplay = SessionSetupValues.Get(this, model => FormatSetupInt(model.FrontHighSpeedRebound));
        NotesPage.ForkSettings.TirePressureDisplay = FormatSetupDouble(session.FrontTirePressure);

        NotesPage.ShockSettings.SpringRateDisplay = SessionSetupValues.Get(this, model => model.RearSpringRate);
        NotesPage.ShockSettings.VolSpcDisplay = SessionSetupValues.Get(this, model => FormatSetupDouble(model.RearVolSpc));
        NotesPage.ShockSettings.HighSpeedCompressionDisplay = SessionSetupValues.Get(this, model => FormatSetupInt(model.RearHighSpeedCompression));
        NotesPage.ShockSettings.LowSpeedCompressionDisplay = SessionSetupValues.Get(this, model => FormatSetupInt(model.RearLowSpeedCompression));
        NotesPage.ShockSettings.LowSpeedReboundDisplay = SessionSetupValues.Get(this, model => FormatSetupInt(model.RearLowSpeedRebound));
        NotesPage.ShockSettings.HighSpeedReboundDisplay = SessionSetupValues.Get(this, model => FormatSetupInt(model.RearHighSpeedRebound));
        NotesPage.ShockSettings.TirePressureDisplay = FormatSetupDouble(session.RearTirePressure);
    }

    private static string? FormatSetupDouble(double? value) =>
        value?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

    private static string? FormatSetupInt(int? value) =>
        value?.ToString(System.Globalization.CultureInfo.InvariantCulture);

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

            // Existence probe on the scalar meta row — the wide row would drag in all SVGs.
            var cacheExists = await databaseService.GetSessionCacheMetaAsync(Id) is not null;
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

    // Non-destructive alternative to Apply: Apply crops in place, while this forks a new
    // session containing the cropped data and leaves the original session unchanged.
    private async Task HandleSaveCropAsCopy()
    {
        var databaseService = App.Current?.Services?.GetService<IDatabaseService>();
        Debug.Assert(databaseService != null, nameof(databaseService) + " != null");

        var start = CropPage.CropStartSample;
        var end   = CropPage.CropEndSample;

        if (end - start < 100)
        {
            ErrorMessages.Add("Crop region too short (minimum 100 samples).");
            return;
        }

        try
        {
            IsAnalyzingData = true;

            var fullData = CropPage.FullData ?? await databaseService.GetSessionPsstAsync(Id);
            if (fullData is null) throw new Exception("Session data not found.");

            var cropped = fullData.CreateCroppedCopy(start, end);
            var serialized = MessagePackSerializer.Serialize(cropped);
            var originalName = session.Name ?? "";
            var baseName = originalName.EndsWith(" (crop)")
                ? originalName[..^" (crop)".Length]
                : originalName;
            var copyName = string.IsNullOrEmpty(baseName) ? "Crop" : $"{baseName} (crop)";
            var sampleCount = Math.Max(cropped.Front.Travel?.Length ?? 0, cropped.Rear.Travel?.Length ?? 0);
            var durationSeconds = cropped.SampleRate > 0 ? sampleCount / cropped.SampleRate : 0;
            var timestamp = cropped.SampleRate > 0
                ? session.Timestamp + start / cropped.SampleRate
                : session.Timestamp;

            var newSession = new Session(
                id: Guid.NewGuid(),
                name: copyName,
                description: $"Cropped copy of '{originalName}'",
                setup: session.Setup,
                timestamp: timestamp)
            {
                ProcessedData = serialized,
                FrontSpringRate = session.FrontSpringRate,
                RearSpringRate = session.RearSpringRate,
                FrontVolSpc = session.FrontVolSpc,
                RearVolSpc = session.RearVolSpc,
                FrontHighSpeedCompression = session.FrontHighSpeedCompression,
                RearHighSpeedCompression = session.RearHighSpeedCompression,
                FrontLowSpeedCompression = session.FrontLowSpeedCompression,
                RearLowSpeedCompression = session.RearLowSpeedCompression,
                FrontLowSpeedRebound = session.FrontLowSpeedRebound,
                RearLowSpeedRebound = session.RearLowSpeedRebound,
                FrontHighSpeedRebound = session.FrontHighSpeedRebound,
                RearHighSpeedRebound = session.RearHighSpeedRebound,
                FrontTirePressure = session.FrontTirePressure,
                RearTirePressure = session.RearTirePressure,
                DurationSeconds = durationSeconds
                // SourceIdentifier intentionally remains null because it is the import-dedup key
                // for the original telemetry file.
            };

            await databaseService.PutSessionAsync(newSession);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var mainPagesViewModel = App.Current?.Services?.GetService<MainPagesViewModel>();
                var svm = new SessionViewModel(newSession, true);
                mainPagesViewModel?.SessionsPage.Source.AddOrUpdate(svm);
                _ = Task.Run(() => svm.PrecomputeCache(cropped));
            });

            IsCropVisible = false;
        }
        catch (Exception e)
        {
            ErrorMessages.Add($"Could not save crop as copy: {e.Message}");
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
            // Scalar meta suffices for the band comparison — no need to pull the wide SVG row
            // just to decide that (usually) nothing moved.
            var meta = await databaseService.GetSessionCacheMetaAsync(Id);
            if (meta is null || !meta.HasPitchBalance || meta.BalanceMetricsJson is null) return;
            var metrics = JsonSerializer.Deserialize<BalanceMetrics>(meta.BalanceMetricsJson);
            if (metrics is null) return;
            if (PitchBandMatches(await ComputeExpectedPitchBandAsync(metrics),
                    meta.PitchExpectedMinDeg, meta.PitchExpectedMaxDeg)) return;

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

            // The pending list shown in the Notes page belongs to the previous setup;
            // reload it for the new one so stale entries are not re-persisted under it.
            NotesPage.LoadPending(await databaseService.GetPendingSetupChangesAsync(newSetup.Id));

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
            var swTotal = Stopwatch.StartNew();
            LastKnownBounds = bounds;
            if (IsCombinedSession)
                PopulateNotesSetup();
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

            SessionCacheMeta? meta;
            using (PerfLog.Measure("load/meta"))
            {
                meta = await databaseService.GetSessionCacheMetaAsync(Id);
            }

            // Re-open fast path: the pages still hold a complete render of the current cache
            // row and the scalar meta confirms that row is still current — skip the wide row
            // fetch and the SVG re-parse entirely. Any token change (PlotVersion, crop, pitch
            // band) or a deleted cache row falls through to the full path below.
            if (_pagesPopulated && await IsCacheMetaCurrentAsync(meta))
            {
                EnsureFullDataLoaded(databaseService);
                InitializeTimeZoomIfNeeded();
                PerfLog.Log($"load/reopen {Id}", swTotal.Elapsed.TotalMilliseconds);
                return;
            }

            var (cacheLoaded, hasVdc, hasPvc) = await LoadCache(meta);

            // Use cache-row flags (hasVdc/hasPvc) instead of in-memory properties —
            // the background lazy-load task hasn't set DamperPage/MiscPage properties yet.
            var needsRecreate = !cacheLoaded ||
                ((SpringPage.FrontTravelHistogram is not null || SpringPage.RearTravelHistogram is not null) && !hasVdc) ||
                (SpringPage.TravelComparisonHistogram is not null && SpringPage.FrontRearTravelScatter is null) ||
                !hasPvc;

            var needsSummary = SummaryPage.RunDataRows.Count == 0;

            // Only hit the DB if we actually need to rebuild cache or summary.
            // Task.Run: the blob deserialize inside GetSessionPsstAsync would otherwise run
            // as a UI-thread continuation of this UI-initiated command.
            if (needsRecreate || needsSummary)
            {
                var fullData = await Task.Run(() => databaseService.GetSessionPsstAsync(Id));

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
                    var summaryData = await SessionSummaryBuilder.PopulateSummary(this, fullData);

                    var cache = await databaseService.GetSessionCacheAsync(Id);
                    if (cache is not null)
                    {
                        cache.SummaryJson = JsonSerializer.Serialize(summaryData);
                        await databaseService.PutSessionCacheAsync(cache);
                    }
                }
            }

            // Valid-cache path: seed the crop slider bounds from the scalar meta when the row
            // carries them (no blob deserialize on the open path); rows written before the
            // sample_rate/sample_count columns existed are seeded inside the deferred load.
            if (!needsRecreate)
            {
                if (CropPage.TotalSamples == 0 && meta is { SampleRate: > 0, SampleCount: > 0 })
                {
                    CropPage.SampleRate    = meta.SampleRate.Value;
                    CropPage.TotalSamples  = meta.SampleCount.Value;
                    CropPage.OriginalStartSample = session.CropStartSample ?? 0;
                    CropPage.OriginalEndSample   = session.CropEndSample   ?? meta.SampleCount.Value;
                    CropPage.CropStartSample = CropPage.OriginalStartSample;
                    CropPage.CropEndSample   = CropPage.OriginalEndSample;
                    CropPage.ViewBounds  = LastKnownBounds;
                }

                // Full telemetry (CropPage.FullData, zoom mini-map) loads deferred, after the
                // SVG parse burst — the open path no longer waits for the blob deserialize.
                EnsureFullDataLoaded(databaseService);
            }

            _pagesPopulated = true;

            // (Re)initialise the shared time-zoom state and context mini-map. No-ops while the
            // deferred full-data load is still pending — it re-triggers this on completion.
            InitializeTimeZoomIfNeeded();
            PerfLog.Log($"load/total {Id}", swTotal.Elapsed.TotalMilliseconds);
        }
        catch (Exception e)
        {
            ErrorMessages.Add($"Could not load session data: {e.Message}");
        }
    }

    protected override bool CanExportPdf() => IsComplete;

    protected override Task ExportPdf() => SessionPdfExporter.ExportAsync(this, essential: false);

    // Distinct name (rather than nameof(CanExportPdf) again) sidesteps MVVMTK0010: the source
    // generator treats the inherited virtual and this type's override of CanExportPdf as two
    // separate matches for a nameof() lookup within this class.
    private bool CanExportPdfEssential() => CanExportPdf();

    // Reduced customer-facing report: Spring/Damper/Balance highlights only, no FFTs,
    // pitch/G-out diagnostics, phase-portrait plots, or Misc time-series pages.
    [RelayCommand(CanExecute = nameof(CanExportPdfEssential))]
    private Task ExportPdfEssential() => SessionPdfExporter.ExportAsync(this, essential: true);

    #endregion
}
