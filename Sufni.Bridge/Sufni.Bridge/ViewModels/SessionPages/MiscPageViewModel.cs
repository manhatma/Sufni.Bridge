using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Svg.Skia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sufni.Bridge.Models;

namespace Sufni.Bridge.ViewModels.SessionPages;

public partial class MiscPageViewModel() : PageViewModelBase("Misc")
{
    // Typed CommandParameter literals for NudgeTrackOffsetCommand. Avalonia parses a bare
    // CommandParameter="1000" as the *string* "1000" (there is no numeric-literal markup
    // extension), and CommunityToolkit's RelayCommand<int> matches its parameter via "is int"
    // pattern matching with no string conversion — so a literal string parameter throws at
    // runtime. Bind via {x:Static vm:MiscPageViewModel.NudgeMinus5000} etc. instead.
    public const int NudgeMinus5000 = -5000;
    public const int NudgeMinus1000 = -1000;
    public const int NudgePlus1000 = 1000;
    public const int NudgePlus5000 = 5000;

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrackTimeOffsetLabel))]
    private long trackTimeOffsetMs;

    [ObservableProperty] private bool trackOffsetAvailable;

    public string TrackTimeOffsetLabel
    {
        get
        {
            var seconds = TrackTimeOffsetMs / 1000.0;
            return $"GPS offset {seconds:+0.0;-0.0;0.0} s";
        }
    }

    public event EventHandler? OverlayMetricChanged;
    public event EventHandler? TrackTimeOffsetChanged;

    /// <summary>
    /// Raised by the "Auto" button. Re-runs <see cref="Sufni.Bridge.Models.TrackTimeOffsetEstimator"/>
    /// against every session assigned to this track — the import-time estimate only ever ran for
    /// tracks imported after the feature landed, so already-imported tracks need this entry point.
    /// </summary>
    public event EventHandler? TrackAutoOffsetRequested;

    private bool updatingMetrics;
    private bool updatingOffset;

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

    partial void OnTrackTimeOffsetMsChanged(long value)
    {
        if (updatingOffset) return;
        TrackTimeOffsetChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void NudgeTrackOffset(int deltaMs)
    {
        TrackTimeOffsetMs += deltaMs;
    }

    [RelayCommand]
    private void ResetTrackOffset()
    {
        TrackTimeOffsetMs = 0;
    }

    [RelayCommand]
    private void AutoTrackOffset()
    {
        TrackAutoOffsetRequested?.Invoke(this, EventArgs.Empty);
    }

    internal void SetTrackOffset(long offsetMs, bool available)
    {
        updatingOffset = true;
        try
        {
            TrackTimeOffsetMs = offsetMs;
            TrackOffsetAvailable = available;
        }
        finally
        {
            updatingOffset = false;
        }
    }
}
