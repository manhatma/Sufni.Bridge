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
    private const int PreviewHeight = 120;
    private const int FullWidth = 800;
    private const int FullHeight = 500;
    private const int ZoomDebounceMs = 280;

    private readonly SessionViewModel viewModel;
    private readonly TimeZoomViewModel timeZoom;
    private CancellationTokenSource? renderCts;
    private SessionTrack? sessionTrack;
    private SKBitmap? tiles;
    private Guid? loadedTrackId;
    private TelemetryData? telemetry;
    private TrackOverlay? currentOverlay;

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
        renderCts?.Cancel();
        var cts = new CancellationTokenSource();
        renderCts = cts;
        var token = cts.Token;

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
    }

    private void OnZoomWindowChanged(object? sender, EventArgs e)
    {
        if (sessionTrack is null || sessionTrack.IsEmpty)
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

        if (loadedTrackId != session.Track || sessionTrack is null)
        {
            var track = await databaseService.GetTrackAsync(session.Track.Value);
            if (track?.Points is null || track.Points.Length == 0)
            {
                ClearBitmaps();
                return;
            }

            var points = MessagePackSerializer.Deserialize<TrackPoints>(track.Points);
            var slices = await BuildSlicesAsync(session, databaseService);
            sessionTrack = SessionTrack.Build(points, slices);
            loadedTrackId = session.Track;
            tiles?.Dispose();
            tiles = null;
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

        if (telemetry is null)
        {
            try
            {
                telemetry = await Task.Run(() => databaseService.GetSessionPsstAsync(session.Id), token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                telemetry = null;
            }
        }

        if (token.IsCancellationRequested) return;

        var available = TrackOverlaySampler.AvailableMetrics(telemetry);
        var selected = viewModel.MiscPage.SelectedOverlayMetric?.Metric ?? TrackOverlayMetric.GpsSpeed;
        if (!TrackOverlaySampler.IsAvailable(selected, telemetry))
            selected = TrackOverlayMetric.GpsSpeed;
        currentOverlay = TrackOverlaySampler.Build(sessionTrack, telemetry, selected);

        Dispatcher.UIThread.Post(() =>
        {
            if (token.IsCancellationRequested) return;
            viewModel.MiscPage.SetAvailableMetrics(available, selected);
        });

        RenderPreviewAndFull(CurrentHighlight(), token);
    }

    private static async Task<IReadOnlyList<WallClockSlice>> BuildSlicesAsync(
        Session session,
        IDatabaseService databaseService)
    {
        var sourceIds = await databaseService.GetCombinedSourcesAsync(session.Id);
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

        var allSessions = await databaseService.GetSessionsAsync();
        var byId = allSessions.ToDictionary(s => s.Id);
        var slices = new List<WallClockSlice>(sourceIds.Count);
        double offset = 0;
        foreach (var sourceId in sourceIds)
        {
            if (!byId.TryGetValue(sourceId, out var source))
                continue;
            var durationSeconds = source.DurationSeconds ?? 0;
            slices.Add(new WallClockSlice(
                (long)(source.Timestamp ?? 0) * 1000,
                (long)durationSeconds * 1000,
                offset));
            offset += durationSeconds;
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

    private void RenderPreviewAndFull(ZoomWindow? highlight, CancellationToken token)
    {
        if (sessionTrack is null) return;

        var preview = TrackMapRenderer.Render(sessionTrack, tiles, PreviewWidth, PreviewHeight, highlight: null, overlay: null);
        if (token.IsCancellationRequested)
        {
            preview.Dispose();
            return;
        }

        var full = TrackMapRenderer.Render(sessionTrack, tiles, FullWidth, FullHeight, highlight, currentOverlay);
        if (token.IsCancellationRequested)
        {
            preview.Dispose();
            full.Dispose();
            return;
        }

        AssignBitmaps(preview, full, token);
    }

    private void RenderFullMap(ZoomWindow? highlight, CancellationToken token)
    {
        if (sessionTrack is null) return;

        var full = TrackMapRenderer.Render(sessionTrack, tiles, FullWidth, FullHeight, highlight, currentOverlay);
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

    private void AssignBitmaps(Bitmap preview, Bitmap full, CancellationToken token)
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
