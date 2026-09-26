using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Svg.Skia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sufni.Bridge.Models;

namespace Sufni.Bridge.ViewModels.SessionPages;

/// <summary>
/// Rectangle in pixels of the rendered map bitmap. Kept free of SkiaSharp types so the view
/// model stays independent of the renderer.
/// </summary>
public readonly record struct MapPixelRect(double X, double Y, double Width, double Height);

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

    // Typed CommandParameter literals for SelectAggregateCommand. Avalonia parses a bare
    // CommandParameter="Avg" as the *string* "Avg" (there is no enum-literal markup
    // extension), and CommunityToolkit's RelayCommand<TrackOverlayAggregate> matches its
    // parameter via "is TrackOverlayAggregate" pattern matching with no string conversion —
    // so a literal string parameter throws at runtime. Bind via
    // {x:Static vm:MiscPageViewModel.AggregateAvg} etc. instead.
    public static readonly TrackOverlayAggregate AggregateAvg = TrackOverlayAggregate.Avg;
    public static readonly TrackOverlayAggregate AggregateP95 = TrackOverlayAggregate.P95;
    public static readonly TrackOverlayAggregate AggregateMax = TrackOverlayAggregate.Max;

    public const double MarkerNudgeSeconds = 0.25;

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

    [ObservableProperty] private TrackOverlayAggregate overlayAggregate = TrackOverlayAggregate.Max;

    [ObservableProperty] private bool aggregateAvailable;

    [ObservableProperty] private bool markerVisible;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MarkerShown))]
    private bool markerOnTrack;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MarkerShown))]
    private bool markerEnabled = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MarkerTimeLabel))]
    private double markerSeconds;
    [ObservableProperty] private double markerX;
    [ObservableProperty] private double markerY;
    // Einheitsvektor der Fahrtrichtung in Pixeln des gerenderten Kartenbildes.
    // Der Data-Marker richtet seine Fahne senkrecht dazu aus.
    [ObservableProperty] private double markerDirX;
    [ObservableProperty] private double markerDirY;
    // Area occupied by the respective statistic marker including its leader and flag;
    // null when that marker is absent. The data marker dodges these areas.
    [ObservableProperty] private MapPixelRect? statMaxBounds;
    [ObservableProperty] private MapPixelRect? statMinBounds;
    [ObservableProperty] private double mapPixelWidth;
    [ObservableProperty] private double mapPixelHeight;
    [ObservableProperty] private string markerValueLabel = "—";
    // Colour of the edge the marker sits on; grey when there is no value there.
    [ObservableProperty] private IBrush markerFill = Brushes.Gray;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrackTimeOffsetLabel))]
    private long trackTimeOffsetMs;

    [ObservableProperty] private bool trackOffsetAvailable;

    public bool IsAvgActive => OverlayAggregate == TrackOverlayAggregate.Avg;
    public bool IsP95Active => OverlayAggregate == TrackOverlayAggregate.P95;
    public bool IsMaxActive => OverlayAggregate == TrackOverlayAggregate.Max;

    public string MarkerTimeLabel => FmtTime(MarkerSeconds);

    // Dot, leader and flag are only drawn when the marker sits on an edge AND the user has not
    // switched it off by tapping the slider.
    public bool MarkerShown => MarkerEnabled && MarkerOnTrack;

    // The marker may sit anywhere the zoomed map actually draws, which is the zoom window plus
    // TimeZoomViewModel.MapContextSeconds of lead-in and run-out. Binding this range to the bare
    // window instead would let every pan step drag the marker along with the window edge, because
    // the slider coerces its value back into [Minimum, Maximum]. With the wider range the marker
    // keeps its own time while panning and only gives way once it would leave the drawn map.
    public double MarkerRangeStart => TimeZoom is { IsZoomActive: true } zoom
        ? Math.Max(0, zoom.StartSeconds - zoom.MapContextSeconds)
        : 0;

    public double MarkerRangeEnd => TimeZoom is { IsZoomActive: true } zoom
        ? Math.Min(zoom.TotalDurationSeconds, zoom.WindowEndSeconds + zoom.MapContextSeconds)
        : 0;

    public string TrackTimeOffsetLabel
    {
        get
        {
            var seconds = TrackTimeOffsetMs / 1000.0;
            return $"GPS offset {seconds:+0.0;-0.0;0.0} s";
        }
    }

    public event EventHandler? OverlayMetricChanged;
    public event EventHandler? OverlayAggregateChanged;
    public event EventHandler? MarkerChanged;
    public event EventHandler? TrackTimeOffsetChanged;

    /// <summary>
    /// Raised by the "Auto" button. Re-runs <see cref="Sufni.Bridge.Models.TrackTimeOffsetEstimator"/>
    /// against every session assigned to this track — the import-time estimate only ever ran for
    /// tracks imported after the feature landed, so already-imported tracks need this entry point.
    /// </summary>
    public event EventHandler? TrackAutoOffsetRequested;

    private bool updatingMetrics;
    private bool updatingOffset;
    private bool updatingMarker;
    private bool wasZoomActive;

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
            AggregateAvailable = SelectedOverlayMetric is not null
                                 && TrackOverlaySampler.SupportsAggregate(SelectedOverlayMetric.Metric);
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
            AggregateAvailable = false;
        }
        finally
        {
            updatingMetrics = false;
        }
    }

    partial void OnSelectedOverlayMetricChanged(TrackOverlayMetricOption? value)
    {
        AggregateAvailable = value is not null && TrackOverlaySampler.SupportsAggregate(value.Metric);
        if (updatingMetrics) return;
        OverlayMetricChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnOverlayAggregateChanged(TrackOverlayAggregate value)
    {
        OnPropertyChanged(nameof(IsAvgActive));
        OnPropertyChanged(nameof(IsP95Active));
        OnPropertyChanged(nameof(IsMaxActive));
        // No suppression flag here: the aggregate is only ever set by the selector buttons,
        // never programmatically, so every change is a user request to rebuild the overlay.
        OverlayAggregateChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnTrackTimeOffsetMsChanged(long value)
    {
        if (updatingOffset) return;
        TrackTimeOffsetChanged?.Invoke(this, EventArgs.Empty);
    }

    // Two-parameter hook so the previous TimeZoom can be unsubscribed. The single-parameter
    // overload only sees the new value.
    partial void OnTimeZoomChanged(TimeZoomViewModel? oldValue, TimeZoomViewModel? newValue)
    {
        if (oldValue is not null)
            oldValue.WindowChanged -= OnZoomWindowChanged;
        if (newValue is not null)
            newValue.WindowChanged += OnZoomWindowChanged;
        OnZoomWindowChanged(newValue, EventArgs.Empty);
    }

    partial void OnTrackMapChanged(Bitmap? value)
    {
        MarkerVisible = TimeZoom is { IsZoomActive: true } && value is not null;
    }

    partial void OnMarkerSecondsChanged(double value)
    {
        if (updatingMarker) return;
        MarkerChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnZoomWindowChanged(object? sender, EventArgs e)
    {
        var zoomActive = TimeZoom is { IsZoomActive: true };
        MarkerVisible = zoomActive && TrackMap is not null;
        OnPropertyChanged(nameof(MarkerRangeStart));
        OnPropertyChanged(nameof(MarkerRangeEnd));

        var start = MarkerRangeStart;
        var end = MarkerRangeEnd;
        var zoomJustActivated = zoomActive && !wasZoomActive;
        double? target = null;
        if (zoomJustActivated)
        {
            // A fresh zoom starts in the middle of the window, not of the wider drawn range.
            target = (TimeZoom!.StartSeconds + TimeZoom.WindowEndSeconds) / 2.0;
        }
        else if (end > start && (MarkerSeconds < start || MarkerSeconds > end))
        {
            // Panning has pushed the marker off the drawn map. Clamp it to the edge it left
            // through; jumping to the middle would throw its position away for no reason.
            target = Math.Clamp(MarkerSeconds, start, end);
        }

        if (target is { } value)
        {
            updatingMarker = true;
            try
            {
                MarkerSeconds = value;
            }
            finally
            {
                updatingMarker = false;
            }
        }

        wasZoomActive = zoomActive;
        MarkerChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void SelectAggregate(TrackOverlayAggregate aggregate) => OverlayAggregate = aggregate;

    [RelayCommand]
    private void NudgeMarkerBack() =>
        MarkerSeconds = Math.Max(MarkerRangeStart, MarkerSeconds - MarkerNudgeSeconds);

    [RelayCommand]
    private void NudgeMarkerForward() =>
        MarkerSeconds = Math.Min(MarkerRangeEnd, MarkerSeconds + MarkerNudgeSeconds);

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

    private static string FmtTime(double totalSeconds)
    {
        if (totalSeconds < 0 || double.IsNaN(totalSeconds)) totalSeconds = 0;
        var minutes = (int)(totalSeconds / 60);
        var seconds = totalSeconds % 60;
        return $"{minutes}:{seconds:00.0}";
    }
}
