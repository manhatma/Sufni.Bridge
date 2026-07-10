using Avalonia.Svg.Skia;
using CommunityToolkit.Mvvm.ComponentModel;

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
}
