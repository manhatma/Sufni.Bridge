using Avalonia.Svg.Skia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sufni.Bridge.ViewModels.SessionPages;

public partial class DamperPageViewModel() : PageViewModelBase("Damper")
{
    [ObservableProperty] private SvgImage? velocityDistributionComparison;
    [ObservableProperty] private SvgImage? frontVelocityHistogram;
    [ObservableProperty] private SvgImage? frontLowSpeedVelocityHistogram;
    [ObservableProperty] private SvgImage? rearVelocityHistogram;
    [ObservableProperty] private SvgImage? rearDamperVelocityHistogram;
    [ObservableProperty] private SvgImage? rearLowSpeedVelocityHistogram;
    [ObservableProperty] private SvgImage? frontVelocityTimeCropped;
    [ObservableProperty] private SvgImage? rearVelocityTimeCropped;

    // While zoomed, front+rear velocity are drawn together in this single plot; the two separate
    // velocity images above are nulled to hide them (IsNotNull visibility bindings).
    [ObservableProperty] private SvgImage? combinedVelocityTimeZoomed;
    [ObservableProperty] private double? frontHscPercentage;
    [ObservableProperty] private double? rearHscPercentage;
    [ObservableProperty] private double? frontLscPercentage;
    [ObservableProperty] private double? rearLscPercentage;
    [ObservableProperty] private double? frontLsrPercentage;
    [ObservableProperty] private double? rearLsrPercentage;
    [ObservableProperty] private double? frontHsrPercentage;
    [ObservableProperty] private double? rearHsrPercentage;

    // Shared session-wide time-zoom state (one instance across Spring/Damper/Misc), assigned by
    // SessionViewModel. Drives the TimeZoomControl placed under the velocity-over-time plots.
    [ObservableProperty] private TimeZoomViewModel? timeZoom;
}
