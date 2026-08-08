using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Svg.Skia;
using Avalonia.Threading;
using ScottPlot;
using Sufni.Bridge.Models;
using Sufni.Bridge.Models.Telemetry;
using Sufni.Bridge.Plots;
using Sufni.Bridge.ViewModels.SessionPages;
using static Sufni.Bridge.Extensions.SvgHelpers;

namespace Sufni.Bridge.ViewModels.Items;

internal sealed class SessionTimeZoomRenderer
{
    private readonly SessionViewModel _viewModel;
    private readonly TimeZoomViewModel _timeZoom;
    private readonly Func<TelemetryData?> _getAnalysisData;
    private readonly Action<TelemetryData?> _setAnalysisData;
    private readonly Func<CancellationTokenSource?> _getZoomRenderCts;
    private readonly Action<CancellationTokenSource?> _setZoomRenderCts;
    private readonly Func<(SvgImage? frontTravel, SvgImage? rearTravel,
        SvgImage? frontVelocity, SvgImage? rearVelocity,
        SvgImage? frontAccel, SvgImage? rearAccel)> _getFullTimePlots;
    private readonly Action<(SvgImage? frontTravel, SvgImage? rearTravel,
        SvgImage? frontVelocity, SvgImage? rearVelocity,
        SvgImage? frontAccel, SvgImage? rearAccel)> _setFullTimePlots;
    private readonly Func<bool> _getTimeZoomSnapshotTaken;
    private readonly Action<bool> _setTimeZoomSnapshotTaken;

    internal SessionTimeZoomRenderer(
        SessionViewModel viewModel,
        TimeZoomViewModel timeZoom,
        Func<TelemetryData?> getAnalysisData,
        Action<TelemetryData?> setAnalysisData,
        Func<CancellationTokenSource?> getZoomRenderCts,
        Action<CancellationTokenSource?> setZoomRenderCts,
        Func<(SvgImage? frontTravel, SvgImage? rearTravel,
            SvgImage? frontVelocity, SvgImage? rearVelocity,
            SvgImage? frontAccel, SvgImage? rearAccel)> getFullTimePlots,
        Action<(SvgImage? frontTravel, SvgImage? rearTravel,
            SvgImage? frontVelocity, SvgImage? rearVelocity,
            SvgImage? frontAccel, SvgImage? rearAccel)> setFullTimePlots,
        Func<bool> getTimeZoomSnapshotTaken,
        Action<bool> setTimeZoomSnapshotTaken)
    {
        _viewModel = viewModel;
        _timeZoom = timeZoom;
        _getAnalysisData = getAnalysisData;
        _setAnalysisData = setAnalysisData;
        _getZoomRenderCts = getZoomRenderCts;
        _setZoomRenderCts = setZoomRenderCts;
        _getFullTimePlots = getFullTimePlots;
        _setFullTimePlots = setFullTimePlots;
        _getTimeZoomSnapshotTaken = getTimeZoomSnapshotTaken;
        _setTimeZoomSnapshotTaken = setTimeZoomSnapshotTaken;
    }

    internal void Subscribe() => _timeZoom.WindowChanged += OnZoomWindowChanged;

    // Analysis data actually plotted in the time-series charts: the cropped copy when the session is
    // cropped, else the full data. Derived once from CropPage.FullData and memoized (CreateCroppedCopy
    // re-smooths, so it is not free); _analysisData is nulled whenever the crop changes.
    private TelemetryData? EnsureAnalysisData()
    {
        var analysisData = _getAnalysisData();
        if (analysisData is not null) return analysisData;
        var full = _viewModel.CropPage.FullData;
        if (full is null) return null;
        var session = _viewModel.SessionModel;
        analysisData = session.CropStartSample.HasValue && session.CropEndSample.HasValue
            ? full.CreateCroppedCopy(session.CropStartSample.Value, session.CropEndSample.Value)
            : full;
        _setAnalysisData(analysisData);
        return analysisData;
    }

    // Runs once per data-load: EnsureAnalysisData sets _analysisData on first success, so later Loaded
    // re-entries skip. Crop apply/reset null _analysisData to force a rebuild.
    internal void InitializeTimeZoomIfNeeded()
    {
        if (_getAnalysisData() is not null) return;
        InitializeTimeZoom();
    }

    // (Re)initialises the shared zoom state for the current analysis data: session duration, context
    // mini-map, and window reset to full/off. Call on the UI thread.
    internal void InitializeTimeZoom()
    {
        _setTimeZoomSnapshotTaken(false);
        _setFullTimePlots((null, null, null, null, null, null));

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
        var b = SessionViewModel.LastKnownBounds;
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
        _getZoomRenderCts()?.Cancel();

        if (!_timeZoom.IsZoomActive)
        {
            RestoreFullTimePlots();
            return;
        }

        SnapshotFullTimePlotsIfNeeded();

        var cts = new CancellationTokenSource();
        _setZoomRenderCts(cts);
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
        if (_getTimeZoomSnapshotTaken()) return;
        _setFullTimePlots((
            _viewModel.SpringPage.FrontTravelTimeCropped,
            _viewModel.SpringPage.RearTravelTimeCropped,
            _viewModel.DamperPage.FrontVelocityTimeCropped,
            _viewModel.DamperPage.RearVelocityTimeCropped,
            _viewModel.MiscPage.FrontAccelerationTimeCropped,
            _viewModel.MiscPage.RearAccelerationTimeCropped));
        _setTimeZoomSnapshotTaken(true);
    }

    private void RestoreFullTimePlots()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_getTimeZoomSnapshotTaken())
            {
                var fullTimePlots = _getFullTimePlots();
                _viewModel.SpringPage.FrontTravelTimeCropped     = fullTimePlots.frontTravel;
                _viewModel.SpringPage.RearTravelTimeCropped      = fullTimePlots.rearTravel;
                _viewModel.DamperPage.FrontVelocityTimeCropped   = fullTimePlots.frontVelocity;
                _viewModel.DamperPage.RearVelocityTimeCropped    = fullTimePlots.rearVelocity;
                _viewModel.MiscPage.FrontAccelerationTimeCropped = fullTimePlots.frontAccel;
                _viewModel.MiscPage.RearAccelerationTimeCropped  = fullTimePlots.rearAccel;
            }
            _viewModel.SpringPage.CombinedTravelTimeZoomed     = null;
            _viewModel.DamperPage.CombinedVelocityTimeZoomed   = null;
            _viewModel.MiscPage.CombinedAccelerationTimeZoomed = null;
        });
    }

    // Renders the six time-series plots zoomed to [winStart, winEnd] from the in-memory analysis data.
    // Background thread; each plot is posted to its page as it finishes, with the token checked between
    // plots so a superseding pan abandons stale work. Does not touch the DB cache.
    private void RenderTimePlotsForWindow(double winStart, double winEnd, CancellationToken token)
    {
        var data = _getAnalysisData();
        if (data is null) return;

        var b = SessionViewModel.LastKnownBounds;
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
                   img => { _viewModel.SpringPage.CombinedTravelTimeZoomed = img; _viewModel.SpringPage.FrontTravelTimeCropped = null; _viewModel.SpringPage.RearTravelTimeCropped = null; });

            Render(() => { var p = new VelocityTimeCombinedPlot(new Plot(), winStart, winEnd); p.LoadTelemetryData(data); return p.Plot.GetSvgXml(width, height); },
                   img => { _viewModel.DamperPage.CombinedVelocityTimeZoomed = img; _viewModel.DamperPage.FrontVelocityTimeCropped = null; _viewModel.DamperPage.RearVelocityTimeCropped = null; });

            Render(() => { var p = new AccelerationTimeCombinedPlot(new Plot(), winStart, winEnd); p.LoadTelemetryData(data); return p.Plot.GetSvgXml(width, height); },
                   img => { _viewModel.MiscPage.CombinedAccelerationTimeZoomed = img; _viewModel.MiscPage.FrontAccelerationTimeCropped = null; _viewModel.MiscPage.RearAccelerationTimeCropped = null; });
        }
    }
}
