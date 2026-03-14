using CommunityToolkit.Mvvm.ComponentModel;

namespace Sufni.Bridge.ViewModels.SessionPages;

public partial class MiscPageViewModel() : PageViewModelBase("Misc")
{
    [ObservableProperty] private string? velocityDistributionComparison;
    [ObservableProperty] private string? positionVelocityComparison;
    [ObservableProperty] private string? frontPositionVelocity;
    [ObservableProperty] private string? rearPositionVelocity;
}
