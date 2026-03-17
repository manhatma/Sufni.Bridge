using Avalonia.Svg.Skia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sufni.Bridge.ViewModels.SessionPages;

public partial class MiscPageViewModel() : PageViewModelBase("Misc")
{
    [ObservableProperty] private SvgImage? velocityDistributionComparison;
    [ObservableProperty] private SvgImage? positionVelocityComparison;
    [ObservableProperty] private SvgImage? frontPositionVelocity;
    [ObservableProperty] private SvgImage? rearPositionVelocity;
}
