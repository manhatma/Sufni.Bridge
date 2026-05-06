using Avalonia.Svg.Skia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sufni.Bridge.ViewModels.SessionPages;

public partial class MiscPageViewModel() : PageViewModelBase("Misc")
{
    [ObservableProperty] private SvgImage? frontAccelerationTimeCropped;
    [ObservableProperty] private SvgImage? rearAccelerationTimeCropped;
    [ObservableProperty] private SvgImage? positionVelocityComparison;
    [ObservableProperty] private SvgImage? frontPositionVelocity;
    [ObservableProperty] private SvgImage? rearPositionVelocity;
    [ObservableProperty] private SvgImage? frontDamperCurve;
    [ObservableProperty] private SvgImage? rearDamperCurve;
    [ObservableProperty] private SvgImage? frontSpringCurve;
    [ObservableProperty] private SvgImage? rearSpringCurve;
}
