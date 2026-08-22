using Avalonia.Svg.Skia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sufni.Bridge.ViewModels.SessionPages;

public partial class DamperPageViewModel() : PageViewModelBase("Damper")
{
    [ObservableProperty] private SvgImage? velocityDistributionComparison;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveFrontVelocityHistogram))]
    private SvgImage? frontVelocityHistogram;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveFrontVelocityHistogram))]
    private SvgImage? frontVelocityHistogramPower;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveFrontVelocityHistogram))]
    private SvgImage? frontVelocityHistogramPowerLog;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveFrontLowSpeedVelocityHistogram))]
    private SvgImage? frontLowSpeedVelocityHistogram;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveFrontLowSpeedVelocityHistogram))]
    private SvgImage? frontLowSpeedVelocityHistogramLog;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveRearVelocityHistogram))]
    private SvgImage? rearVelocityHistogram;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveRearVelocityHistogram))]
    private SvgImage? rearVelocityHistogramPower;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveRearVelocityHistogram))]
    private SvgImage? rearVelocityHistogramPowerLog;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveRearDamperVelocityHistogram))]
    private SvgImage? rearDamperVelocityHistogram;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveRearDamperVelocityHistogram))]
    private SvgImage? rearDamperVelocityHistogramPower;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveRearDamperVelocityHistogram))]
    private SvgImage? rearDamperVelocityHistogramPowerLog;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveRearLowSpeedVelocityHistogram))]
    private SvgImage? rearLowSpeedVelocityHistogram;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveRearLowSpeedVelocityHistogram))]
    private SvgImage? rearLowSpeedVelocityHistogramLog;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveFrontVelocityHistogram))]
    [NotifyPropertyChangedFor(nameof(EffectiveRearVelocityHistogram))]
    [NotifyPropertyChangedFor(nameof(EffectiveRearDamperVelocityHistogram))]
    private bool powerMode = true;
    // Log Y for the power histograms — only meaningful while PowerMode is on. Default off (linear).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveFrontVelocityHistogram))]
    [NotifyPropertyChangedFor(nameof(EffectiveRearVelocityHistogram))]
    [NotifyPropertyChangedFor(nameof(EffectiveRearDamperVelocityHistogram))]
    private bool powerLogMode;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveFrontLowSpeedVelocityHistogram))]
    [NotifyPropertyChangedFor(nameof(EffectiveRearLowSpeedVelocityHistogram))]
    private bool logMode = true;
    [ObservableProperty] private SvgImage? frontVelocityTimeCropped;
    [ObservableProperty] private SvgImage? rearVelocityTimeCropped;

    public SvgImage? EffectiveFrontVelocityHistogram => PowerMode ? (PowerLogMode ? FrontVelocityHistogramPowerLog : FrontVelocityHistogramPower) : FrontVelocityHistogram;
    public SvgImage? EffectiveRearVelocityHistogram => PowerMode ? (PowerLogMode ? RearVelocityHistogramPowerLog : RearVelocityHistogramPower) : RearVelocityHistogram;
    public SvgImage? EffectiveRearDamperVelocityHistogram => PowerMode ? (PowerLogMode ? RearDamperVelocityHistogramPowerLog : RearDamperVelocityHistogramPower) : RearDamperVelocityHistogram;
    public SvgImage? EffectiveFrontLowSpeedVelocityHistogram => LogMode ? FrontLowSpeedVelocityHistogramLog : FrontLowSpeedVelocityHistogram;
    public SvgImage? EffectiveRearLowSpeedVelocityHistogram => LogMode ? RearLowSpeedVelocityHistogramLog : RearLowSpeedVelocityHistogram;

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
