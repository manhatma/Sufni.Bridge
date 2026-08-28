using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Sufni.Bridge.Models;
using Sufni.Bridge.Models.Telemetry;
using Sufni.Bridge.Plots;
using Sufni.Bridge.Services;
using Sufni.Bridge.ViewModels.SessionPages;

namespace Sufni.Bridge.ViewModels.Items;

internal sealed class SessionMapRenderer
{
    // The summary preview now fills the space above RUN DATA, so it is rendered close to that
    // aspect (~393x340 pt on an iPhone 15) instead of the old letterbox strip.
    private const int PreviewWidth = 700;
    private const int PreviewHeight = 600;
    // Portrait: the Misc map fills the screen below the metric dropdown (~393x645 pt on an
    // iPhone 15 Pro), so it is rendered at that aspect and fitted with Stretch=Uniform.
    private const int FullWidth = 620;
    private const int FullHeight = 1000;
    private const int ZoomDebounceMs = 280;

    private readonly SessionViewModel viewModel;
    private readonly TimeZoomViewModel timeZoom;
    // loadCts: ReloadAsync / EnsureLoadedAsync. renderCts: zoom debounce and metric change.
    // Zoom/metric must not cancel an in-flight load — that dropped SetAvailableMetrics and
    // left RenderFullMap to paint a monotone track (cold start vs. post-import).
    private CancellationTokenSource? loadCts;
    private CancellationTokenSource? renderCts;
    private volatile bool loadInProgress;
    private SessionTrack? sessionTrack;
    // Two mosaics. overview* covers the whole track and feeds the Summary-page preview and the
    // unzoomed full map. focus* covers just the zoomed sub-range, fetched at whatever tile zoom
    // that extent warrants, so the zoomed map shows sharp imagery instead of upscaled overview
    // tiles. Both must be dropped whenever the track geometry changes.
    private SKBitmap? overviewTiles;
    private MapBounds? overviewBounds;
    private SKBitmap? focusTiles;
    private MapBounds? focusMosaicBounds;
    private Guid? loadedTrackId;
    private TelemetryData? telemetry;
    private TrackOverlay? currentOverlay;
    private bool isCombined;
    // Cached so a manual offset nudge can rebuild the session track without re-reading the DB.
    private TrackPoints? cachedPoints;
    private IReadOnlyList<WallClockSlice>? cachedSlices;
    private Track? loadedTrack;

    internal SessionMapRenderer(SessionViewModel viewModel, TimeZoomViewModel timeZoom)
    {
        this.viewModel = viewModel;
        this.timeZoom = timeZoom;
    }

    internal void Subscribe()
    {
        timeZoom.WindowChanged += OnZoomWindowChanged;
        viewModel.MiscPage.OverlayMetricChanged += OnOverlayMetricChanged;
        viewModel.MiscPage.TrackTimeOffsetChanged += OnTrackTimeOffsetChanged;
        viewModel.MiscPage.TrackAutoOffsetRequested += OnTrackAutoOffsetRequested;
    }

    internal void Invalidate()
    {
        loadedTrackId = null;
        sessionTrack = null;
        telemetry = null;
        currentOverlay = null;
        cachedPoints = null;
        cachedSlices = null;
        loadedTrack = null;
        DropTiles();
    }

    private void DropTiles()
    {
        overviewTiles?.Dispose();
        overviewTiles = null;
        overviewBounds = null;
        DropFocusTiles();
    }

    private void DropFocusTiles()
    {
        focusTiles?.Dispose();
        focusTiles = null;
        focusMosaicBounds = null;
    }

    internal Task EnsureLoadedAsync() => ReloadAsync();

    internal async Task ReloadAsync()
    {
        loadCts?.Cancel();
        renderCts?.Cancel();
        var cts = new CancellationTokenSource();
        loadCts = cts;
        var token = cts.Token;
        loadInProgress = true;

        try
        {
            await LoadAndRenderAsync(token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
                viewModel.ErrorMessages.Add($"Could not load track map: {ex.Message}"));
        }
        finally
        {
            if (ReferenceEquals(loadCts, cts))
                loadInProgress = false;
        }
    }

    private void OnZoomWindowChanged(object? sender, EventArgs e)
    {
        if (sessionTrack is null || sessionTrack.IsEmpty)
            return;
        // LoadAndRenderAsync already samples CurrentHighlight() at paint time.
        if (loadInProgress)
            return;

        renderCts?.Cancel();
        var cts = new CancellationTokenSource();
        renderCts = cts;
        var token = cts.Token;
        var highlight = CurrentHighlight();
        var focusWindow = CurrentFocusWindow();

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ZoomDebounceMs, token);
                if (token.IsCancellationRequested) return;
                // The window moved, so the map's extent moved with it — the focus mosaic has to
                // follow before the map is repainted.
                var focus = FocusBoundsFor(sessionTrack, focusWindow);
                await EnsureFocusTilesAsync(focus, token);
                if (token.IsCancellationRequested) return;
                RenderFullMap(highlight, focus, token);
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private async Task LoadAndRenderAsync(CancellationToken token)
    {
        var session = viewModel.SessionModel;
        if (session.Track is null)
        {
            ClearBitmaps();
            return;
        }

        var databaseService = App.Current?.Services?.GetService<IDatabaseService>();
        var tileService = App.Current?.Services?.GetService<IMapTileService>();
        if (databaseService is null)
            return;

        var needTrack = loadedTrackId != session.Track || sessionTrack is null;
        if (needTrack || telemetry is null)
        {
            TelemetryData? fullTelemetry = null;
            try
            {
                fullTelemetry = await Task.Run(() => databaseService.GetSessionPsstAsync(session.Id), token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                fullTelemetry = null;
            }

            if (needTrack)
            {
                var track = await databaseService.GetTrackAsync(session.Track.Value);
                if (track?.Points is null || track.Points.Length == 0)
                {
                    ClearBitmaps();
                    return;
                }

                var points = MessagePackSerializer.Deserialize<TrackPoints>(track.Points);
                var combinedSourceIds = await databaseService.GetCombinedSourcesAsync(session.Id);
                isCombined = combinedSourceIds.Count > 0;
                var slices = await BuildSlicesAsync(session, databaseService, fullTelemetry);
                cachedPoints = points;
                cachedSlices = slices;
                loadedTrack = track;
                sessionTrack = SessionTrack.Build(points, slices, track.TimeOffsetMs);
                loadedTrackId = session.Track;
                DropTiles();
            }

            telemetry = ToOverlayTelemetry(session, fullTelemetry);
        }

        if (sessionTrack is null || sessionTrack.IsEmpty)
        {
            ClearBitmaps();
            return;
        }

        await EnsureOverviewTilesAsync(token);

        if (token.IsCancellationRequested) return;

        var available = TrackOverlaySampler.AvailableMetrics(telemetry);
        var selected = viewModel.MiscPage.SelectedOverlayMetric?.Metric ?? TrackOverlayMetric.GpsSpeed;
        if (!TrackOverlaySampler.IsAvailable(selected, telemetry))
            selected = TrackOverlayMetric.GpsSpeed;
        currentOverlay = TrackOverlaySampler.Build(sessionTrack, telemetry, selected);

        var focus = FocusBoundsFor(sessionTrack, CurrentFocusWindow());
        await EnsureFocusTilesAsync(focus, token);

        RenderPreviewAndFull(CurrentHighlight(), focus, available, selected, token);
    }

    private static int FullSampleCount(TelemetryData? data)
    {
        if (data is null) return 0;
        var front = data.Front?.Travel?.Length ?? 0;
        var rear = data.Rear?.Travel?.Length ?? 0;
        return Math.Max(front, rear);
    }

    private static bool ShouldApplyCrop(Session session, TelemetryData? fullTelemetry)
    {
        if (fullTelemetry is null) return false;
        if (fullTelemetry.SampleRate <= 0) return false;
        if (FullSampleCount(fullTelemetry) <= 0) return false;
        return session.CropStartSample.HasValue && session.CropEndSample.HasValue;
    }

    private static TelemetryData? ToOverlayTelemetry(Session session, TelemetryData? fullTelemetry)
    {
        if (fullTelemetry is null) return null;
        if (!ShouldApplyCrop(session, fullTelemetry))
            return fullTelemetry;
        return fullTelemetry.CreateCroppedCopy(session.CropStartSample!.Value, session.CropEndSample!.Value);
    }

    private static async Task<IReadOnlyList<WallClockSlice>> BuildSlicesAsync(
        Session session,
        IDatabaseService databaseService,
        TelemetryData? fullTelemetry)
    {
        // Flatten first: this used to walk only one level of combined sources and treat each source
        // as one contiguous wall-clock block. For a session combined from combined sessions that
        // block covers just the first few runs, and every track after it fell outside the window
        // and vanished from the map.
        var allSessions = await databaseService.GetSessionsAsync();
        var byId = allSessions.ToDictionary(s => s.Id);
        var combinedIds = await databaseService.GetAllCombinedIdsAsync();
        var rate = fullTelemetry?.SampleRate ?? 0;
        // Per-leaf rate from the cache, falling back to this session's own rate: every recording on
        // one DAQ shares it, so the fallback is right whenever a leaf was never cached.
        var cachedRates = await databaseService.GetSampleRatesAsync();
        var flattened = await TrackTimeline.FlattenAsync(
            session.Id, databaseService.GetCombinedSourcesAsync, byId, combinedIds,
            sessionStartSeconds: 0,
            sampleRateFor: id => cachedRates.TryGetValue(id, out var r) && r > 0 ? r : rate);

        if (!ShouldApplyCrop(session, fullTelemetry))
            return flattened;

        // The outer session carries its own crop. Keep the part of each flattened window that
        // survives it, and rebase the session-time offsets on the crop's start.
        var cropStartSec = (session.CropStartSample ?? 0) / (double)rate;
        var cropEndSec = (session.CropEndSample ?? FullSampleCount(fullTelemetry)) / (double)rate;

        var slices = new List<WallClockSlice>(flattened.Count);
        foreach (var slice in flattened)
        {
            var offsetSec = slice.SessionStartSeconds;
            var durSec = slice.DurationMs / 1000.0;
            var overlapStart = Math.Max(offsetSec, cropStartSec);
            var overlapEnd = Math.Min(offsetSec + durSec, cropEndSec);
            if (overlapEnd <= overlapStart)
                continue;

            slices.Add(new WallClockSlice(
                slice.StartUnixMs + (long)((overlapStart - offsetSec) * 1000),
                (long)((overlapEnd - overlapStart) * 1000),
                overlapStart - cropStartSec));
        }

        return slices;
    }

    private ZoomWindow? CurrentHighlight()
    {
        if (!timeZoom.IsZoomActive)
            return null;
        return new ZoomWindow
        {
            StartSeconds = timeZoom.StartSeconds,
            EndSeconds = timeZoom.WindowEndSeconds
        };
    }

    // The map frames the zoom window plus TimeZoomViewModel.MapContextSeconds of lead-in and
    // run-out, so the approach into the zoomed stretch and the run-out stay visible. Sampled on
    // the UI thread as plain seconds; the geographic box is derived later, against whichever
    // track version is current.
    private (double Start, double End)? CurrentFocusWindow()
    {
        if (!timeZoom.IsZoomActive)
            return null;
        var start = timeZoom.StartSeconds;
        var end = timeZoom.WindowEndSeconds;
        if (end <= start)
            return null;
        var context = timeZoom.MapContextSeconds;
        if (context <= 0)
            return null;
        return (start - context, end + context);
    }

    // Null means "frame the whole track": no zoom, or the window falls inside a GPS gap and there
    // is no track geometry to zoom to.
    private static MapBounds? FocusBoundsFor(SessionTrack? track, (double Start, double End)? window)
    {
        if (track is null || track.IsEmpty || window is null)
            return null;
        return track.BoundsForWindow(window.Value.Start, window.Value.End);
    }

    private async Task EnsureOverviewTilesAsync(CancellationToken token)
    {
        if (overviewTiles is not null || sessionTrack is null || sessionTrack.IsEmpty)
            return;
        var tileService = App.Current?.Services?.GetService<IMapTileService>();
        if (tileService is null)
            return;

        try
        {
            var previewExtent = sessionTrack.Bounds.PadAndFit(
                MapBounds.DefaultPad, PreviewWidth / (double)PreviewHeight);
            var fullExtent = sessionTrack.Bounds.PadAndFit(
                MapBounds.DefaultPad, FullWidth / (double)FullHeight);
            var mosaic = await tileService.GetMosaicAsync(
                MapBounds.Union(previewExtent, fullExtent), token);
            overviewTiles = mosaic?.Bitmap;
            overviewBounds = mosaic?.Bounds;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            overviewTiles = null;
            overviewBounds = null;
        }
    }

    // Refetches when the cached focus mosaic no longer covers the needed extent, or when it is
    // more than twice as wide — otherwise panning would keep upscaling a mosaic fetched at a
    // coarser tile zoom. MapTileService caches tiles in memory and on disk, so panning back over
    // an area already visited costs no network.
    private async Task EnsureFocusTilesAsync(MapBounds? focus, CancellationToken token)
    {
        if (focus is null)
        {
            DropFocusTiles();
            return;
        }

        var needed = focus.PadAndFit(MapBounds.DefaultPad, FullWidth / (double)FullHeight);
        if (focusTiles is not null && focusMosaicBounds is not null &&
            Covers(focusMosaicBounds, needed) &&
            focusMosaicBounds.Width <= needed.Width * 2.0)
            return;

        var tileService = App.Current?.Services?.GetService<IMapTileService>();
        if (tileService is null)
        {
            DropFocusTiles();
            return;
        }

        try
        {
            var mosaic = await tileService.GetMosaicAsync(needed, token);
            DropFocusTiles();
            focusTiles = mosaic?.Bitmap;
            focusMosaicBounds = mosaic?.Bounds;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            DropFocusTiles();
        }
    }

    private static bool Covers(MapBounds outer, MapBounds inner) =>
        outer.MinX <= inner.MinX && outer.MaxX >= inner.MaxX &&
        outer.MinY <= inner.MinY && outer.MaxY >= inner.MaxY;

    // A failed focus fetch is not fatal: the geometry still zooms, the overview mosaic just gets
    // upscaled into it.
    private Bitmap RenderFull(ZoomWindow? highlight, MapBounds? focus)
    {
        var useFocus = focus is not null && focusTiles is not null;
        return TrackMapRenderer.Render(
            sessionTrack!,
            useFocus ? focusTiles : overviewTiles,
            useFocus ? focusMosaicBounds : overviewBounds,
            FullWidth, FullHeight,
            highlight, currentOverlay, drawMarkers: !isCombined,
            focusBounds: focus);
    }

    private void RenderPreviewAndFull(
        ZoomWindow? highlight,
        MapBounds? focus,
        IReadOnlyList<TrackOverlayMetric> available,
        TrackOverlayMetric selected,
        CancellationToken token)
    {
        if (sessionTrack is null) return;

        // The preview always shows the whole track — it is the session overview on another page.
        var preview = TrackMapRenderer.Render(sessionTrack, overviewTiles, overviewBounds, PreviewWidth, PreviewHeight, highlight: null, overlay: null, drawMarkers: false, plainTrackColor: new SKColor(0xF2, 0x6A, 0x21), drawDirectionArrow: true, haloTrack: true);
        if (token.IsCancellationRequested)
        {
            preview.Dispose();
            return;
        }

        var full = RenderFull(highlight, focus);
        if (token.IsCancellationRequested)
        {
            preview.Dispose();
            full.Dispose();
            return;
        }

        AssignBitmaps(preview, full, available, selected, token);
    }

    private void RenderFullMap(ZoomWindow? highlight, MapBounds? focus, CancellationToken token)
    {
        if (sessionTrack is null) return;

        var full = RenderFull(highlight, focus);
        if (token.IsCancellationRequested)
        {
            full.Dispose();
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (token.IsCancellationRequested)
            {
                full.Dispose();
                return;
            }

            viewModel.TrackMap?.Dispose();
            viewModel.TrackMap = full;
            viewModel.MiscPage.TrackMap = full;
        });
    }

    private void AssignBitmaps(
        Bitmap preview,
        Bitmap full,
        IReadOnlyList<TrackOverlayMetric> available,
        TrackOverlayMetric selected,
        CancellationToken token)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (token.IsCancellationRequested)
            {
                preview.Dispose();
                full.Dispose();
                return;
            }

            viewModel.TrackMapPreview?.Dispose();
            viewModel.TrackMap?.Dispose();
            viewModel.TrackMapPreview = preview;
            viewModel.TrackMap = full;
            viewModel.SummaryPage.TrackMapPreview = preview;
            viewModel.MiscPage.TrackMap = full;
            viewModel.MiscPage.SetTrackOffset(loadedTrack?.TimeOffsetMs ?? 0, true);
            // ComboBox IsVisible is bound to TrackMap. Populate after the control is shown
            // so ItemsSource changes apply on a realized ComboBox (cold-start path).
            viewModel.MiscPage.SetAvailableMetrics(available, selected);
        });
    }

    private void ClearBitmaps()
    {
        // No track to show — the mosaics fetched for the previous one are worthless.
        DropTiles();
        Dispatcher.UIThread.Post(() =>
        {
            viewModel.TrackMapPreview?.Dispose();
            viewModel.TrackMap?.Dispose();
            viewModel.TrackMapPreview = null;
            viewModel.TrackMap = null;
            viewModel.SummaryPage.TrackMapPreview = null;
            viewModel.MiscPage.TrackMap = null;
            viewModel.MiscPage.ClearOverlayMetrics();
            viewModel.MiscPage.SetTrackOffset(0, false);
        });
    }

    // Estimates the GPX/SST clock offset from every session assigned to this track and writes the
    // result into the view model, which then takes the normal OnTrackTimeOffsetChanged path
    // (rebuild + re-render + persist). Leaves the current value untouched when the day's data is
    // too thin for an estimate — see TrackTimeOffsetEstimator.
    private void OnTrackAutoOffsetRequested(object? sender, EventArgs e)
    {
        var points = cachedPoints;
        var track = loadedTrack;
        if (points is null || track is null || loadInProgress)
            return;

        Task.Run(async () =>
        {
            try
            {
                var databaseService = App.Current?.Services?.GetService<IDatabaseService>();
                if (databaseService is null)
                    return;

                var allSessions = await databaseService.GetSessionsAsync();
                var byId = allSessions.ToDictionary(s => s.Id);
                var combinedIds = await databaseService.GetAllCombinedIdsAsync();
                var cachedRates = await databaseService.GetSampleRatesAsync();
                double RateFor(Guid id) => cachedRates.TryGetValue(id, out var r) ? r : 0;
                var intervals = new List<WallClockSlice>();
                foreach (var candidate in allSessions)
                {
                    if (candidate.Track != track.Id) continue;
                    intervals.AddRange(
                        await TrackTimeline.FlattenAsync(
                            candidate.Id, databaseService.GetCombinedSourcesAsync, byId, combinedIds,
                            sampleRateFor: RateFor));
                }

                var estimated = TrackTimeOffsetEstimator.Estimate(points, intervals);
                Dispatcher.UIThread.Post(() =>
                {
                    if (estimated is null)
                    {
                        viewModel.ErrorMessages.Add(
                            "Could not estimate the GPS offset: not enough descending sessions on this track.");
                        return;
                    }

                    viewModel.MiscPage.TrackTimeOffsetMs = estimated.Value;
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                    viewModel.ErrorMessages.Add($"Could not estimate the GPS offset: {ex.Message}"));
            }
        });
    }

    private void OnTrackTimeOffsetChanged(object? sender, EventArgs e)
    {
        if (sessionTrack is null || sessionTrack.IsEmpty)
            return;
        if (loadInProgress)
            return;

        renderCts?.Cancel();
        var cts = new CancellationTokenSource();
        renderCts = cts;
        var token = cts.Token;
        var newOffsetMs = viewModel.MiscPage.TrackTimeOffsetMs;
        var highlight = CurrentHighlight();
        var focusWindow = CurrentFocusWindow();
        var selected = viewModel.MiscPage.SelectedOverlayMetric?.Metric ?? TrackOverlayMetric.GpsSpeed;
        var points = cachedPoints;
        var slices = cachedSlices;
        var track = loadedTrack;
        var data = telemetry;
        if (points is null || slices is null || track is null)
            return;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ZoomDebounceMs, token);
                if (token.IsCancellationRequested) return;

                // Publish the new offset on the cached Track *before* rendering: RenderPreviewAndFull
                // posts AssignBitmaps to the UI thread, and that pushes loadedTrack.TimeOffsetMs back
                // into the view model. With the old value still on the Track it would undo the nudge.
                track.TimeOffsetMs = newOffsetMs;
                sessionTrack = SessionTrack.Build(points, slices, newOffsetMs);
                if (!TrackOverlaySampler.IsAvailable(selected, data))
                    selected = TrackOverlayMetric.GpsSpeed;
                // Per-edge overlay values depend on track geometry — rebuild, do not reuse.
                currentOverlay = TrackOverlaySampler.Build(sessionTrack, data, selected);
                var available = TrackOverlaySampler.AvailableMetrics(data);

                // Shifting the track in time moves it geographically. The mosaic fetched for the
                // old bounds no longer covers it, and the uncovered part painted as flat
                // background — so both mosaics are dropped and refetched here.
                DropTiles();
                await EnsureOverviewTilesAsync(token);
                if (token.IsCancellationRequested) return;
                var focus = FocusBoundsFor(sessionTrack, focusWindow);
                await EnsureFocusTilesAsync(focus, token);
                if (token.IsCancellationRequested) return;
                RenderPreviewAndFull(highlight, focus, available, selected, token);

                var databaseService = App.Current?.Services?.GetService<IDatabaseService>();
                if (databaseService is not null)
                    await databaseService.PutTrackAsync(track);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                    viewModel.ErrorMessages.Add($"Could not update track offset: {ex.Message}"));
            }
        }, token);
    }

    private void OnOverlayMetricChanged(object? sender, EventArgs e)
    {
        if (sessionTrack is null || sessionTrack.IsEmpty)
            return;
        if (loadInProgress)
            return;

        renderCts?.Cancel();
        var cts = new CancellationTokenSource();
        renderCts = cts;
        var token = cts.Token;
        var highlight = CurrentHighlight();
        var focusWindow = CurrentFocusWindow();
        var metric = viewModel.MiscPage.SelectedOverlayMetric?.Metric ?? TrackOverlayMetric.GpsSpeed;
        var track = sessionTrack;
        var data = telemetry;

        Task.Run(async () =>
        {
            try
            {
                // Colours only — the geometry and therefore both mosaics stay valid.
                currentOverlay = TrackOverlaySampler.Build(track, data, metric);
                if (token.IsCancellationRequested) return;
                var focus = FocusBoundsFor(track, focusWindow);
                await EnsureFocusTilesAsync(focus, token);
                if (token.IsCancellationRequested) return;
                RenderFullMap(highlight, focus, token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                    viewModel.ErrorMessages.Add($"Could not render track overlay: {ex.Message}"));
            }
        }, token);
    }
}
