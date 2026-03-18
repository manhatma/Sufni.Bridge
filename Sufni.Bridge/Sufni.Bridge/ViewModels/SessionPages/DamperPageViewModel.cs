using Avalonia.Svg.Skia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sufni.Bridge.ViewModels.SessionPages;

public partial class DamperPageViewModel() : PageViewModelBase("Damper")
{
    [ObservableProperty] private SvgImage? velocityDistributionComparison;
    [ObservableProperty] private SvgImage? frontVelocityHistogram;
    [ObservableProperty] private SvgImage? rearVelocityHistogram;
    [ObservableProperty] private double? frontHscPercentage;
    [ObservableProperty] private double? rearHscPercentage;
    [ObservableProperty] private double? frontLscPercentage;
    [ObservableProperty] private double? rearLscPercentage;
    [ObservableProperty] private double? frontLsrPercentage;
    [ObservableProperty] private double? rearLsrPercentage;
    [ObservableProperty] private double? frontHsrPercentage;
    [ObservableProperty] private double? rearHsrPercentage;
}
