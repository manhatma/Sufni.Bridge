using Avalonia.Svg.Skia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sufni.Bridge.ViewModels.SessionPages;

public partial class MiscPageViewModel() : PageViewModelBase("Misc")
{
    [ObservableProperty] private SvgImage? travelTimeCropped;
    [ObservableProperty] private SvgImage? velocityTimeCropped;
    [ObservableProperty] private SvgImage? accelerationTimeCropped;
    [ObservableProperty] private SvgImage? positionVelocityComparison;
    [ObservableProperty] private SvgImage? frontPositionVelocity;
    [ObservableProperty] private SvgImage? rearPositionVelocity;
}
