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
    private const int PreviewWidth = 400;
    private const int PreviewHeight = 180;
    private const int FullWidth = 800;
    private const int FullHeight = 500;
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
    private SKBitmap? tiles;
    private Guid? loadedTrackId;
    private TelemetryData? telemetry;
    private TrackOverlay? currentOverlay;
    private bool isCombined;

    internal SessionMapRenderer(SessionViewModel viewModel, TimeZoomViewModel timeZoom)
    {
        this.viewModel = viewModel;
        this.timeZoom = timeZoom;
    }

    internal void Subscribe()
    {
        timeZoom.WindowChanged += OnZoomWindowChanged;
        viewModel.MiscPage.OverlayMetricChanged += OnOverlayMetricChanged;
    }

    internal void Invalidate()
    {
        loadedTrackId = null;
        sessionTrack = null;
        telemetry = null;
        currentOverlay = null;
        tiles?.Dispose();
        tiles = null;
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

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ZoomDebounceMs, token);
                if (token.IsCancellationRequested) return;
                RenderFullMap(highlight, token);
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
                sessionTrack = SessionTrack.Build(points, slices);
                loadedTrackId = session.Track;
                tiles?.Dispose();
                tiles = null;
            }

            telemetry = ToOverlayTelemetry(session, fullTelemetry);
        }

        if (sessionTrack is null || sessionTrack.IsEmpty)
        {
            ClearBitmaps();
            return;
        }

        if (tiles is null && tileService is not null)
        {
            try
            {
                tiles = await tileService.GetMosaicAsync(sessionTrack.Bounds, token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                tiles = null;
            }
        }

        if (token.IsCancellationRequested) return;

        var available = TrackOverlaySampler.AvailableMetrics(telemetry);
        var selected = viewModel.MiscPage.SelectedOverlayMetric?.Metric ?? TrackOverlayMetric.GpsSpeed;
        if (!TrackOverlaySampler.IsAvailable(selected, telemetry))
            selected = TrackOverlayMetric.GpsSpeed;
        currentOverlay = TrackOverlaySampler.Build(sessionTrack, telemetry, selected);

        RenderPreviewAndFull(CurrentHighlight(), available, selected, token);
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
        var sourceIds = await databaseService.GetCombinedSourcesAsync(session.Id);
        if (!ShouldApplyCrop(session, fullTelemetry))
        {
            if (sourceIds.Count == 0)
            {
                return
                [
                    new WallClockSlice(
                        (long)(session.Timestamp ?? 0) * 1000,
                        (long)(session.DurationSeconds ?? 0) * 1000,
                        0)
                ];
            }

            var allSessionsUncut = await databaseService.GetSessionsAsync();
            var byIdUncut = allSessionsUncut.ToDictionary(s => s.Id);
            var uncut = new List<WallClockSlice>(sourceIds.Count);
            double offsetUncut = 0;
            foreach (var sourceId in sourceIds)
            {
                if (!byIdUncut.TryGetValue(sourceId, out var source))
                    continue;
                var durationSeconds = source.DurationSeconds ?? 0;
                uncut.Add(new WallClockSlice(
                    (long)(source.Timestamp ?? 0) * 1000,
                    (long)durationSeconds * 1000,
                    offsetUncut));
                offsetUncut += durationSeconds;
            }

            return uncut;
        }

        var rate = fullTelemetry!.SampleRate;
        var cropStart = session.CropStartSample ?? 0;
        var cropEnd = session.CropEndSample ?? FullSampleCount(fullTelemetry);
        var cropStartSec = cropStart / (double)rate;
        var cropEndSec = cropEnd / (double)rate;

        var sources = new List<(double wallStart, double offsetSec, double durSec)>();
        if (sourceIds.Count == 0)
        {
            sources.Add((session.Timestamp ?? 0, 0, session.DurationSeconds ?? 0));
        }
        else
        {
            var allSessions = await databaseService.GetSessionsAsync();
            var byId = allSessions.ToDictionary(s => s.Id);
            double offset = 0;
            foreach (var sourceId in sourceIds)
            {
                if (!byId.TryGetValue(sourceId, out var source))
                    continue;
                var durationSeconds = source.DurationSeconds ?? 0;
                sources.Add((source.Timestamp ?? 0, offset, durationSeconds));
                offset += durationSeconds;
            }
        }

        var slices = new List<WallClockSlice>(sources.Count);
        foreach (var (wallStart, offsetSec, durSec) in sources)
        {
            var overlapStart = Math.Max(offsetSec, cropStartSec);
            var overlapEnd = Math.Min(offsetSec + durSec, cropEndSec);
            if (overlapEnd <= overlapStart)
                continue;

            slices.Add(new WallClockSlice(
                (long)((wallStart + (overlapStart - offsetSec)) * 1000),
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

    private void RenderPreviewAndFull(
        ZoomWindow? highlight,
        IReadOnlyList<TrackOverlayMetric> available,
        TrackOverlayMetric selected,
        CancellationToken token)
    {
        if (sessionTrack is null) return;

        var preview = TrackMapRenderer.Render(sessionTrack, tiles, PreviewWidth, PreviewHeight, highlight: null, overlay: null, drawMarkers: false, plainTrackColor: new SKColor(0xF2, 0x6A, 0x21));
        if (token.IsCancellationRequested)
        {
            preview.Dispose();
            return;
        }

        var full = TrackMapRenderer.Render(sessionTrack, tiles, FullWidth, FullHeight, highlight, currentOverlay, drawMarkers: !isCombined);
        if (token.IsCancellationRequested)
        {
            preview.Dispose();
            full.Dispose();
            return;
        }

        AssignBitmaps(preview, full, available, selected, token);
    }

    private void RenderFullMap(ZoomWindow? highlight, CancellationToken token)
    {
        if (sessionTrack is null) return;

        var full = TrackMapRenderer.Render(sessionTrack, tiles, FullWidth, FullHeight, highlight, currentOverlay, drawMarkers: !isCombined);
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
            // ComboBox IsVisible is bound to TrackMap. Populate after the control is shown
            // so ItemsSource changes apply on a realized ComboBox (cold-start path).
            viewModel.MiscPage.SetAvailableMetrics(available, selected);
        });
    }

    private void ClearBitmaps()
    {
        Dispatcher.UIThread.Post(() =>
        {
            viewModel.TrackMapPreview?.Dispose();
            viewModel.TrackMap?.Dispose();
            viewModel.TrackMapPreview = null;
            viewModel.TrackMap = null;
            viewModel.SummaryPage.TrackMapPreview = null;
            viewModel.MiscPage.TrackMap = null;
            viewModel.MiscPage.ClearOverlayMetrics();
        });
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
        var metric = viewModel.MiscPage.SelectedOverlayMetric?.Metric ?? TrackOverlayMetric.GpsSpeed;
        var track = sessionTrack;
        var data = telemetry;

        Task.Run(() =>
        {
            try
            {
                currentOverlay = TrackOverlaySampler.Build(track, data, metric);
                if (token.IsCancellationRequested) return;
                RenderFullMap(highlight, token);
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
