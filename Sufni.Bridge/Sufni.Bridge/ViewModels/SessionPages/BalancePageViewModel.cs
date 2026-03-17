using Avalonia.Svg.Skia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sufni.Bridge.ViewModels.SessionPages;

public partial class BalancePageViewModel() : PageViewModelBase("Balance")
{
    [ObservableProperty] private SvgImage? compressionBalance;
    [ObservableProperty] private SvgImage? reboundBalance;
}
