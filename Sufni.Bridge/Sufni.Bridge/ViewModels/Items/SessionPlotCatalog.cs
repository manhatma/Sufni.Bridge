using System;
using Avalonia.Svg.Skia;
using ScottPlot;
using Sufni.Bridge.Models;
using Sufni.Bridge.Models.Telemetry;
using Sufni.Bridge.Plots;

namespace Sufni.Bridge.ViewModels.Items;

internal enum PlotSize
{
    Standard,
    LessBandView,
    TravelTimeHistory
}

internal enum PlotGroup
{
    TravelTimeHistory,
    SpringComparison,
    Front,
    Rear,
    Balance,
    TimeCropped,
    Fft,
    Pitch,
    Misc,
    PhasePortrait
}

internal sealed record PlotLateContext(
    Discipline? Discipline,
    double PeakMinHz,
    double PeakMaxHz,
    (double minDeg, double maxDeg)? ExpectedBand);

internal sealed record PlotContext(
    TelemetryData Data,
    TelemetryData FullSource,
    bool IsCombined,
    int Width,
    int Height,
    int TthHeight)
{
    internal PlotLateContext? Late { get; init; }
}

internal sealed record PlotSlot(
    string Label,
    PlotGroup Group,
    Func<PlotContext, bool> Applies,
    Func<PlotContext, SufniPlot> Create,
    Func<PlotContext, TelemetryData> Source,
    PlotSize Size,
    Action<SessionCache, string?> StoreSvg,
    Func<SessionCache, string?> ReadSvg,
    Action<SessionViewModel, SvgImage?> Assign,
    Action<SufniPlot>? PrepareRender = null);

internal static class SessionPlotCatalog
{
    private static bool Always(PlotContext _) => true;
    private static bool Front(PlotContext context) => context.Data.Front.Present;
    private static bool Rear(PlotContext context) => context.Data.Rear.Present;
    private static bool Both(PlotContext context) => context.Data.Front.Present && context.Data.Rear.Present;

    internal static readonly PlotSlot[] Slots =
    [
        new(
            "tth", PlotGroup.TravelTimeHistory, Always,
            _ => new TravelTimeHistoryPlot(new Plot()),
            context => context.FullSource,
            PlotSize.TravelTimeHistory,
            (cache, svg) => cache.TravelTimeHistory = svg,
            cache => cache.TravelTimeHistory,
            (viewModel, image) => viewModel.CropPage.TravelTimeHistory = image,
            plot => plot.Plot.Axes.Title.Label.Text = "Travel over time (full)"),
        new(
            "travelCompHist", PlotGroup.SpringComparison, Both,
            _ => new TravelHistogramComparisonPlot(new Plot()),
            context => context.Data,
            PlotSize.Standard,
            (cache, svg) => cache.TravelComparisonHistogram = svg,
            cache => cache.TravelComparisonHistogram,
            (viewModel, image) => viewModel.SpringPage.TravelComparisonHistogram = image),
        new(
            "frontRearScatter", PlotGroup.SpringComparison, Both,
            _ => new FrontRearTravelScatterPlot(new Plot()),
            context => context.Data,
            PlotSize.Standard,
            (cache, svg) => cache.FrontRearTravelScatter = svg,
            cache => cache.FrontRearTravelScatter,
            (viewModel, image) => viewModel.SpringPage.FrontRearTravelScatter = image),
        new(
            "frontTravelHist", PlotGroup.Front, Front,
            _ => new TravelHistogramPlot(new Plot(), SuspensionType.Front),
            context => context.Data,
            PlotSize.Standard,
            (cache, svg) => cache.FrontTravelHistogram = svg,
            cache => cache.FrontTravelHistogram,
            (viewModel, image) => viewModel.SpringPage.FrontTravelHistogram = image),
        new(
            "frontVelHist", PlotGroup.Front, Front,
            _ => new VelocityHistogramPlot(new Plot(), SuspensionType.Front),
            context => context.Data,
            PlotSize.Standard,
            (cache, svg) => cache.FrontVelocityHistogram = svg,
            cache => cache.FrontVelocityHistogram,
            (viewModel, image) => viewModel.DamperPage.FrontVelocityHistogram = image),
        new(
            "frontLsVelHist", PlotGroup.Front, Front,
            _ => new LowSpeedVelocityHistogramPlot(new Plot(), SuspensionType.Front),
            context => context.Data,
            PlotSize.LessBandView,
            (cache, svg) => cache.FrontLowSpeedVelocityHistogram = svg,
            cache => cache.FrontLowSpeedVelocityHistogram,
            (viewModel, image) => viewModel.DamperPage.FrontLowSpeedVelocityHistogram = image),
        new(
            "rearTravelHist", PlotGroup.Rear, Rear,
            _ => new TravelHistogramPlot(new Plot(), SuspensionType.Rear),
            context => context.Data,
            PlotSize.Standard,
            (cache, svg) => cache.RearTravelHistogram = svg,
            cache => cache.RearTravelHistogram,
            (viewModel, image) => viewModel.SpringPage.RearTravelHistogram = image),
        new(
            "rearVelHist", PlotGroup.Rear, Rear,
            _ => new VelocityHistogramPlot(new Plot(), SuspensionType.Rear),
            context => context.Data,
            PlotSize.Standard,
            (cache, svg) => cache.RearVelocityHistogram = svg,
            cache => cache.RearVelocityHistogram,
            (viewModel, image) => viewModel.DamperPage.RearVelocityHistogram = image),
        new(
            "rearDamperVelHist", PlotGroup.Rear, Rear,
            _ => new DamperVelocityHistogramPlot(new Plot()),
            context => context.Data,
            PlotSize.Standard,
            (cache, svg) => cache.RearDamperVelocityHistogram = svg,
            cache => cache.RearDamperVelocityHistogram,
            (viewModel, image) => viewModel.DamperPage.RearDamperVelocityHistogram = image),
        new(
            "rearLsVelHist", PlotGroup.Rear, Rear,
            _ => new LowSpeedVelocityHistogramPlot(new Plot(), SuspensionType.Rear),
            context => context.Data,
            PlotSize.LessBandView,
            (cache, svg) => cache.RearLowSpeedVelocityHistogram = svg,
            cache => cache.RearLowSpeedVelocityHistogram,
            (viewModel, image) => viewModel.DamperPage.RearLowSpeedVelocityHistogram = image),
        new(
            "combinedBalance", PlotGroup.Balance, Both,
            _ => new CombinedBalancePlot(new Plot()),
            context => context.Data,
            PlotSize.Standard,
            (cache, svg) => cache.CombinedBalance = svg,
            cache => cache.CombinedBalance,
            (viewModel, image) => viewModel.BalancePage.CombinedBalance = image),
        new(
            "compressionBalance", PlotGroup.Balance, Both,
            _ => new BalancePlot(new Plot(), BalanceType.Compression),
            context => context.Data,
            PlotSize.Standard,
            (cache, svg) => cache.CompressionBalance = svg,
            cache => cache.CompressionBalance,
            (viewModel, image) => viewModel.BalancePage.CompressionBalance = image),
        new(
            "reboundBalance", PlotGroup.Balance, Both,
            _ => new BalancePlot(new Plot(), BalanceType.Rebound),
            context => context.Data,
            PlotSize.Standard,
            (cache, svg) => cache.ReboundBalance = svg,
            cache => cache.ReboundBalance,
            (viewModel, image) => viewModel.BalancePage.ReboundBalance = image),
        new(
            "cumulativeTravel", PlotGroup.Balance, Both,
            _ => new CumulativeTravelPlot(new Plot()),
            context => context.Data,
            PlotSize.Standard,
            (cache, svg) => cache.CumulativeTravel = svg,
            cache => cache.CumulativeTravel,
            (viewModel, image) => viewModel.BalancePage.CumulativeTravel = image),
        new(
            "frontTravelTimeCropped", PlotGroup.TimeCropped, Front,
            _ => new TravelTimeCroppedPlot(new Plot(), SuspensionType.Front),
            context => context.Data,
            PlotSize.Standard,
            (cache, svg) => cache.FrontTravelTimeCropped = svg,
            cache => cache.FrontTravelTimeCropped,
            (viewModel, image) => viewModel.SpringPage.FrontTravelTimeCropped = image),
        new(
            "frontVelTimeCropped", PlotGroup.TimeCropped, Front,
            _ => new VelocityTimeCroppedPlot(new Plot(), SuspensionType.Front),
            context => context.Data,
            PlotSize.Standard,
            (cache, svg) => cache.FrontVelocityTimeCropped = svg,
            cache => cache.FrontVelocityTimeCropped,
            (viewModel, image) => viewModel.DamperPage.FrontVelocityTimeCropped = image),
        new(
            "rearTravelTimeCropped", PlotGroup.TimeCropped, Rear,
            _ => new TravelTimeCroppedPlot(new Plot(), SuspensionType.Rear),
            context => context.Data,
            PlotSize.Standard,
            (cache, svg) => cache.RearTravelTimeCropped = svg,
            cache => cache.RearTravelTimeCropped,
            (viewModel, image) => viewModel.SpringPage.RearTravelTimeCropped = image),
        new(
            "rearVelTimeCropped", PlotGroup.TimeCropped, Rear,
            _ => new VelocityTimeCroppedPlot(new Plot(), SuspensionType.Rear),
            context => context.Data,
            PlotSize.Standard,
            (cache, svg) => cache.RearVelocityTimeCropped = svg,
            cache => cache.RearVelocityTimeCropped,
            (viewModel, image) => viewModel.DamperPage.RearVelocityTimeCropped = image),
        new(
            "velFft", PlotGroup.Fft, Both,
            context => new CombinedTravelFftPlot(new Plot(), minHz: 1.0, maxHz: 10.0,
                peakMinHz: context.Late!.PeakMinHz, peakMaxHz: context.Late.PeakMaxHz,
                fitYAxisToData: true, topHeadroomDb: 2.0,
                mode: WheelSpectrumMode.Velocity),
            context => context.Data,
            PlotSize.Standard,
            (cache, svg) => cache.CombinedVelocityFft = svg,
            cache => cache.CombinedVelocityFft,
            (viewModel, image) => viewModel.BalancePage.CombinedVelocityFft = image),
        new(
            "travelFft", PlotGroup.Fft, Both,
            context => new CombinedTravelFftPlot(new Plot(), minHz: 1.0, maxHz: 10.0,
                peakMinHz: context.Late!.PeakMinHz, peakMaxHz: context.Late.PeakMaxHz,
                fitYAxisToData: true, topHeadroomDb: 3.0),
            context => context.Data,
            PlotSize.Standard,
            (cache, svg) => cache.CombinedTravelFft = svg,
            cache => cache.CombinedTravelFft,
            (viewModel, image) => viewModel.BalancePage.CombinedTravelFft = image),
        new(
            "travelFftHigh", PlotGroup.Fft, Both,
            _ => new CombinedTravelFftPlot(new Plot(), minHz: 10.0, maxHz: 100.0,
                peakMinHz: 0.0, peakMaxHz: 0.0, segmentLength: 4096, fitYAxisToData: true,
                lineWidth: 1.5f),
            context => context.Data,
            PlotSize.Standard,
            (cache, svg) => cache.CombinedTravelFftHigh = svg,
            cache => cache.CombinedTravelFftHigh,
            (viewModel, image) => viewModel.BalancePage.CombinedTravelFftHigh = image),
        new(
            "pitchBalance", PlotGroup.Pitch, Both,
            context => new PitchBalancePlot(new Plot(),
                context.Late!.ExpectedBand?.minDeg, context.Late.ExpectedBand?.maxDeg),
            context => context.Data,
            PlotSize.Standard,
            (cache, svg) => cache.PitchBalance = svg,
            cache => cache.PitchBalance,
            (viewModel, image) => viewModel.BalancePage.PitchBalance = image),
        new(
            "pitchCoherence", PlotGroup.Pitch, Both,
            context => new PitchCoherencePlot(new Plot(), context.Late!.Discipline),
            context => context.Data,
            PlotSize.Standard,
            (cache, svg) => cache.PitchCoherence = svg,
            cache => cache.PitchCoherence,
            (viewModel, image) => viewModel.BalancePage.PitchCoherence = image),
        new(
            "goutScatter", PlotGroup.Pitch, Both,
            _ => new GoutScatterPlot(new Plot()),
            context => context.Data,
            PlotSize.Standard,
            (cache, svg) => cache.GoutScatter = svg,
            cache => cache.GoutScatter,
            (viewModel, image) => viewModel.BalancePage.GoutScatter = image),
        new(
            "frontAccel", PlotGroup.Misc, Front,
            _ => new AccelerationTimeCroppedPlot(new Plot(), SuspensionType.Front),
            context => context.Data,
            PlotSize.Standard,
            (cache, svg) => cache.FrontAccelerationTimeCropped = svg,
            cache => cache.FrontAccelerationTimeCropped,
            (viewModel, image) => viewModel.MiscPage.FrontAccelerationTimeCropped = image),
        new(
            "rearAccel", PlotGroup.Misc, Rear,
            _ => new AccelerationTimeCroppedPlot(new Plot(), SuspensionType.Rear),
            context => context.Data,
            PlotSize.Standard,
            (cache, svg) => cache.RearAccelerationTimeCropped = svg,
            cache => cache.RearAccelerationTimeCropped,
            (viewModel, image) => viewModel.MiscPage.RearAccelerationTimeCropped = image),
        new(
            "velDistComp", PlotGroup.Misc, Always,
            _ => new VelocityDistributionComparisonPlot(new Plot()),
            context => context.Data,
            PlotSize.Standard,
            (cache, svg) => cache.VelocityDistributionComparison = svg,
            cache => cache.VelocityDistributionComparison,
            (viewModel, image) => viewModel.DamperPage.VelocityDistributionComparison = image),
        new(
            "posVelComp", PlotGroup.PhasePortrait, context => !context.IsCombined,
            _ => new PositionVelocityComparisonPlot(new Plot()),
            context => context.Data,
            PlotSize.Standard,
            (cache, svg) => cache.PositionVelocityComparison = svg,
            cache => cache.PositionVelocityComparison,
            (viewModel, image) => viewModel.MiscPage.PositionVelocityComparison = image),
        new(
            "frontPosVel", PlotGroup.PhasePortrait, context => !context.IsCombined && context.Data.Front.Present,
            _ => new PositionVelocityPlot(new Plot(), SuspensionType.Front),
            context => context.Data,
            PlotSize.Standard,
            (cache, svg) => cache.FrontPositionVelocity = svg,
            cache => cache.FrontPositionVelocity,
            (viewModel, image) => viewModel.MiscPage.FrontPositionVelocity = image),
        new(
            "rearPosVel", PlotGroup.PhasePortrait, context => !context.IsCombined && context.Data.Rear.Present,
            _ => new PositionVelocityPlot(new Plot(), SuspensionType.Rear),
            context => context.Data,
            PlotSize.Standard,
            (cache, svg) => cache.RearPositionVelocity = svg,
            cache => cache.RearPositionVelocity,
            (viewModel, image) => viewModel.MiscPage.RearPositionVelocity = image)
    ];

    internal static PlotSlot ByLabel(string label) =>
        Array.Find(Slots, slot => slot.Label == label)
        ?? throw new InvalidOperationException($"Unknown session plot slot '{label}'.");

    internal static PlotSlot[] InGroup(PlotGroup group) =>
        Array.FindAll(Slots, slot => slot.Group == group);
}
