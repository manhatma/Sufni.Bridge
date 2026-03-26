using Avalonia.Svg.Skia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sufni.Bridge.ViewModels.SessionPages;

public partial class SpringPageViewModel() : PageViewModelBase("Spring")
{
    [ObservableProperty] private SvgImage? travelComparisonHistogram;
    [ObservableProperty] private SvgImage? frontRearTravelScatter;
    [ObservableProperty] private SvgImage? frontTravelHistogram;
    [ObservableProperty] private SvgImage? rearTravelHistogram;
}
