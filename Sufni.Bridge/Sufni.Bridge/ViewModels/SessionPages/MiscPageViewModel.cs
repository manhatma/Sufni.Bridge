using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Svg.Skia;
using CommunityToolkit.Mvvm.ComponentModel;
using Sufni.Bridge.Models;

namespace Sufni.Bridge.ViewModels.SessionPages;

public partial class MiscPageViewModel() : PageViewModelBase("Misc")
{
    [ObservableProperty] private SvgImage? positionVelocityComparison;
    [ObservableProperty] private SvgImage? frontPositionVelocity;
    [ObservableProperty] private SvgImage? rearPositionVelocity;
    [ObservableProperty] private SvgImage? frontAccelerationTimeCropped;
    [ObservableProperty] private SvgImage? rearAccelerationTimeCropped;

    // While zoomed, front+rear acceleration are drawn together in this single plot; the two
    // separate acceleration images above are nulled to hide them (IsNotNull visibility bindings).
    [ObservableProperty] private SvgImage? combinedAccelerationTimeZoomed;

    // Shared session-wide time-zoom state (one instance across Spring/Damper/Misc), assigned by
    // SessionViewModel. Drives the TimeZoomControl placed under the acceleration-over-time plots.
    [ObservableProperty] private TimeZoomViewModel? timeZoom;

    [ObservableProperty] private Bitmap? trackMap;

    public ObservableCollection<TrackOverlayMetricOption> OverlayMetrics { get; } = [];

    [ObservableProperty] private TrackOverlayMetricOption? selectedOverlayMetric;

    public event EventHandler? OverlayMetricChanged;

    private bool updatingMetrics;

    internal void SetAvailableMetrics(IReadOnlyList<TrackOverlayMetric> metrics, TrackOverlayMetric selected)
    {
        updatingMetrics = true;
        try
        {
            OverlayMetrics.Clear();
            TrackOverlayMetricOption? match = null;
            foreach (var metric in metrics)
            {
                var option = new TrackOverlayMetricOption(metric);
                OverlayMetrics.Add(option);
                if (metric == selected)
                    match = option;
            }

            SelectedOverlayMetric = match ?? (OverlayMetrics.Count > 0 ? OverlayMetrics[0] : null);
        }
        finally
        {
            updatingMetrics = false;
        }
    }

    internal void ClearOverlayMetrics()
    {
        updatingMetrics = true;
        try
        {
            OverlayMetrics.Clear();
            SelectedOverlayMetric = null;
        }
        finally
        {
            updatingMetrics = false;
        }
    }

    partial void OnSelectedOverlayMetricChanged(TrackOverlayMetricOption? value)
    {
        if (updatingMetrics) return;
        OverlayMetricChanged?.Invoke(this, EventArgs.Empty);
    }
}
