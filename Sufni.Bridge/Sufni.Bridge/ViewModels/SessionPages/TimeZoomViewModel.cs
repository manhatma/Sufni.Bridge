using System;
using Avalonia.Svg.Skia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sufni.Bridge.ViewModels.SessionPages;

/// <summary>
/// Shared time-zoom state for suspension-telemetry time-series plots: a fixed window width
/// (0 = off/full view, otherwise 2/5/10 s) panned across the session via StartSeconds. A single
/// instance is bound to the TimeZoomControl placed under the plots on multiple session pages, so
/// panning/zooming stays in sync no matter which page is visible. The integrator sets MiniMap and
/// TotalDurationSeconds, and listens to WindowChanged (debounced) to re-render the zoomed plots.
/// </summary>
public partial class TimeZoomViewModel : ObservableObject
{
    // Typed CommandParameter literals for SelectWindowCommand. Avalonia parses a bare
    // CommandParameter="2" as the *string* "2" (there is no numeric-literal markup extension),
    // and CommunityToolkit's RelayCommand<int> matches its parameter via "is int" pattern
    // matching with no string conversion — so a literal string parameter throws at runtime.
    // Bind via {x:Static vm:TimeZoomViewModel.Window2Seconds} etc. instead, so the parameter
    // that reaches the command is a genuine boxed int.
    public const int WindowOff = 0;
    public const int Window2Seconds = 2;
    public const int Window5Seconds = 5;
    public const int Window10Seconds = 10;

    // Scale fine pan adjustments with the selected window; an unknown/off window keeps the
    // legacy 1 s fallback. The buttons adjust StartSeconds independently of the slider.
    private double PanNudgeStepSeconds => WindowSeconds switch
    {
        Window2Seconds => 0.25,
        Window5Seconds => 0.5,
        Window10Seconds => 1.0,
        _ => 1.0,
    };

    // Per-domain session-overview strips (travel/velocity/acceleration). The TimeZoomControl on each
    // page picks the one matching its page via its MiniMap styled property; all three share the same
    // window state so panning stays in sync across pages.
    [ObservableProperty] private SvgImage? miniMapTravel;
    [ObservableProperty] private SvgImage? miniMapVelocity;
    [ObservableProperty] private SvgImage? miniMapAcceleration;
    [ObservableProperty] private double totalDurationSeconds;
    [ObservableProperty] private int windowSeconds;
    [ObservableProperty] private double startSeconds;
    // Starts hidden; SessionViewModel.InitializeTimeZoom enables it once the session's analysis data
    // is loaded and long enough (> 2 s) to zoom, so the control never flashes a disabled selector.
    [ObservableProperty] private bool isEnabled;

    /// <summary>
    /// Raised whenever the effective window (WindowSeconds and/or StartSeconds) changes, so the
    /// integrator can debounce and re-render the zoomed plots.
    /// </summary>
    public event EventHandler? WindowChanged;

    public TimeZoomViewModel()
    {
    }

    public bool IsZoomActive => WindowSeconds > 0;

    public double MaxStartSeconds => WindowSeconds > 0 ? Math.Max(0, TotalDurationSeconds - WindowSeconds) : 0;

    public double WindowEndSeconds => Math.Min(TotalDurationSeconds, StartSeconds + WindowSeconds);

    public double StartFraction => TotalDurationSeconds > 0 ? StartSeconds / TotalDurationSeconds : 0;

    public double WidthFraction
    {
        get
        {
            if (TotalDurationSeconds <= 0) return 0;
            var width = WindowSeconds / TotalDurationSeconds;
            return Math.Max(0, Math.Min(width, 1 - StartFraction));
        }
    }

    public bool Allow2s => TotalDurationSeconds > 2;
    public bool Allow5s => TotalDurationSeconds > 5;
    public bool Allow10s => TotalDurationSeconds > 10;

    public bool Is2sActive => WindowSeconds == 2;
    public bool Is5sActive => WindowSeconds == 5;
    public bool Is10sActive => WindowSeconds == 10;

    public string StartLabel => FmtTime(StartSeconds);
    public string EndLabel => FmtTime(WindowEndSeconds);

    [RelayCommand]
    private void SelectWindow(int seconds)
    {
        WindowSeconds = WindowSeconds == seconds || seconds == 0 ? 0 : seconds;
    }

    // Fine pan adjustment: OnStartSecondsChanged clamps and fires WindowChanged, so these just set the value.
    [RelayCommand] private void NudgePanBack()    => StartSeconds = Math.Max(0, StartSeconds - PanNudgeStepSeconds);
    [RelayCommand] private void NudgePanForward() => StartSeconds = Math.Min(MaxStartSeconds, StartSeconds + PanNudgeStepSeconds);

    partial void OnWindowSecondsChanged(int value)
    {
        ClampStart();
        NotifyComputed();
    }

    partial void OnStartSecondsChanged(double value)
    {
        ClampStart();
        NotifyComputed();
    }

    partial void OnTotalDurationSecondsChanged(double value)
    {
        ClampStart();
        NotifyComputed();
    }

    // Keeps StartSeconds inside [0, MaxStartSeconds] whenever WindowSeconds or TotalDurationSeconds
    // shrink the valid range. Setting the property (rather than the backing field) re-enters this
    // same handler once more, which terminates immediately: clamping an already-in-range value is
    // a no-op, so there is no infinite recursion — just one extra (harmless) notification pass.
    private void ClampStart()
    {
        var clamped = Math.Clamp(StartSeconds, 0, MaxStartSeconds);
        if (clamped != StartSeconds)
            StartSeconds = clamped;
    }

    // Raises PropertyChanged for every computed/derived property and fires WindowChanged. Called
    // from all three On*Changed hooks; a few extra (unchanged) notifications per call are harmless,
    // whereas missing one would leave the UI stale, so we always refresh the full set.
    private void NotifyComputed()
    {
        OnPropertyChanged(nameof(IsZoomActive));
        OnPropertyChanged(nameof(MaxStartSeconds));
        OnPropertyChanged(nameof(WindowEndSeconds));
        OnPropertyChanged(nameof(StartFraction));
        OnPropertyChanged(nameof(WidthFraction));
        OnPropertyChanged(nameof(Allow2s));
        OnPropertyChanged(nameof(Allow5s));
        OnPropertyChanged(nameof(Allow10s));
        OnPropertyChanged(nameof(StartLabel));
        OnPropertyChanged(nameof(EndLabel));
        OnPropertyChanged(nameof(Is2sActive));
        OnPropertyChanged(nameof(Is5sActive));
        OnPropertyChanged(nameof(Is10sActive));
        WindowChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string FmtTime(double totalSeconds)
    {
        if (totalSeconds < 0 || double.IsNaN(totalSeconds)) totalSeconds = 0;
        var minutes = (int)(totalSeconds / 60);
        var seconds = totalSeconds % 60;
        return $"{minutes}:{seconds:00.0}";
    }
}
