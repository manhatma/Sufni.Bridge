using Avalonia.Svg.Skia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sufni.Bridge.ViewModels.SessionPages;

public partial class SpringPageViewModel() : PageViewModelBase("Spring")
{
    [ObservableProperty] private SvgImage? travelComparisonHistogram;
    [ObservableProperty] private SvgImage? frontRearTravelScatter;
    [ObservableProperty] private SvgImage? frontTravelHistogram;
    [ObservableProperty] private SvgImage? rearTravelHistogram;
    [ObservableProperty] private SvgImage? frontTravelTimeCropped;
    [ObservableProperty] private SvgImage? rearTravelTimeCropped;

    // While zoomed, front+rear travel are drawn together in this single plot; the two separate
    // travel images above are nulled to hide them (IsNotNull visibility bindings).
    [ObservableProperty] private SvgImage? combinedTravelTimeZoomed;

    // Shared session-wide time-zoom state (one instance across Spring/Damper/Misc), assigned by
    // SessionViewModel. Drives the TimeZoomControl placed under the travel-over-time plots.
    [ObservableProperty] private TimeZoomViewModel? timeZoom;
}
