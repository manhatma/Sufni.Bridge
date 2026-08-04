using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using Sufni.Bridge.Extensions;
using Sufni.Bridge.Models;
using Sufni.Bridge.Models.Telemetry;
using Sufni.Bridge.Plots;
using Sufni.Bridge.Services;
using Sufni.Bridge.ViewModels.SessionPages;
using static Sufni.Bridge.Extensions.SvgHelpers;

namespace Sufni.Bridge.ViewModels.Items;

internal static class SessionCacheBuilder
{
    internal static async Task BuildAsync(
        SessionViewModel viewModel,
        object? bounds,
        TelemetryData telemetryData,
        TelemetryData? fullData,
        Session session,
        IDatabaseService databaseService,
        Stopwatch swCache,
        Func<Task<Discipline?>> getSessionDisciplineAsync,
        Func<Discipline?, Task<Dictionary<string, (double? min, double? max)>?>> getBalanceOverridesAsync,
        Func<TelemetryData, Task<VelocityBands?>, Task<VelocityBands?>,
            Task<SessionViewModel.CachedSummaryData>> populateSummaryAsync,
        Func<string, Action, Task> throttledPlotTask)
    {
        var SpringPage = viewModel.SpringPage;
        var DamperPage = viewModel.DamperPage;
        var BalancePage = viewModel.BalancePage;
        var MiscPage = viewModel.MiscPage;
        var Pages = viewModel.Pages;

        // Combined sessions have no telemetry of their own (they're a view over their source
        // sessions' data) — the three phase-portrait plots below are skipped for them entirely
        // (cache columns stay null, MiscPageView hides the images via IsVisible bindings).
        var isCombined = (await databaseService.GetCombinedSourcesAsync(viewModel.Id)).Count > 0;

        var b = (Rect)bounds!;
        var (width, height) = ((int)b.Width, (int)(b.Height / 2.0));
        // Full and cropped time-history share the total vertical budget equally.
        // Previous split: full = height*0.8 (= b.Height*0.4), cropped = width/2.
        // In crop mode the tab bar collapses (~30 px); absorb that into chart heights
        // so the crop sliders keep their absolute Y position.
        const double CollapsedTabBarHeight = 30.0;
        var tthHeight = (int)(((b.Height - CollapsedTabBarHeight) * 0.4 + b.Width / 2.0 + CollapsedTabBarHeight) / 2.0);

        // TravelTimeHistory — always uses full (uncompressed) data
        var tthSource = fullData ?? telemetryData;
        var context = new PlotContext(telemetryData, tthSource, isCombined, width, height, tthHeight);

        var sessionCache = new SessionCache
        {
            SessionId = viewModel.Id,
            PlotVersion = SessionViewModel.CurrentPlotVersion,
            CropStartSample = session.CropStartSample,
            CropEndSample   = session.CropEndSample,
            // Scalar meta for later opens: rate and FULL (uncropped) sample count let the
            // crop slider seed without deserializing the telemetry blob.
            SampleRate  = tthSource.SampleRate,
            SampleCount = tthSource.Front.Present
                ? tthSource.Front.Travel.Length
                : tthSource.Rear.Present ? tthSource.Rear.Travel.Length : 0
        };
        var tasks = new List<Task>();
        var disciplineTask = telemetryData.Front.Present && telemetryData.Rear.Present
            ? getSessionDisciplineAsync()
            : null;

        EnqueueSlotGroup(tasks, context, sessionCache, viewModel, throttledPlotTask, PlotGroup.TravelTimeHistory);

        // Shared VelocityBands tasks — computed once, used by both DamperPage UI and summary
        Task<VelocityBands?> frontBandsTask = Task.FromResult<VelocityBands?>(null);
        Task<VelocityBands?> rearBandsTask = Task.FromResult<VelocityBands?>(null);

        if (telemetryData.Front.Present)
        {
            frontBandsTask = Task.Run(() =>
                (VelocityBands?)telemetryData.CalculateVelocityBands(
                    SuspensionType.Front,
                    200,
                    telemetryData.FrontVelocityDeadBand()));
        }

        if (telemetryData.Rear.Present)
        {
            rearBandsTask = Task.Run(() =>
                (VelocityBands?)telemetryData.CalculateVelocityBands(
                    SuspensionType.Rear,
                    200,
                    telemetryData.RearWheelVelocityDeadBand()));
        }

        if (telemetryData.Front.Present && telemetryData.Rear.Present)
        {
            EnqueueSlotGroup(tasks, context, sessionCache, viewModel, throttledPlotTask, PlotGroup.SpringComparison);
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                SpringPage.TravelComparisonHistogram = null;
                SpringPage.FrontRearTravelScatter = null;
            });
        }

        EnqueueSlotGroup(tasks, context, sessionCache, viewModel, throttledPlotTask, PlotGroup.Front);
        if (telemetryData.Front.Present)
        {
            // Apply shared front VelocityBands to cache + UI
            tasks.Add(frontBandsTask.ContinueWith(t =>
            {
                var fvb = t.Result;
                if (fvb is null) return;
                sessionCache.FrontHsrPercentage = fvb.HighSpeedRebound;
                sessionCache.FrontLsrPercentage = fvb.LowSpeedRebound;
                sessionCache.FrontLscPercentage = fvb.LowSpeedCompression;
                sessionCache.FrontHscPercentage = fvb.HighSpeedCompression;
                Dispatcher.UIThread.Post(() =>
                {
                    DamperPage.FrontHsrPercentage = fvb.HighSpeedRebound;
                    DamperPage.FrontLsrPercentage = fvb.LowSpeedRebound;
                    DamperPage.FrontLscPercentage = fvb.LowSpeedCompression;
                    DamperPage.FrontHscPercentage = fvb.HighSpeedCompression;
                });
            }, TaskScheduler.Default));
        }

        EnqueueSlotGroup(tasks, context, sessionCache, viewModel, throttledPlotTask, PlotGroup.Rear);
        if (telemetryData.Rear.Present)
        {
            // Apply shared rear VelocityBands to cache + UI
            tasks.Add(rearBandsTask.ContinueWith(t =>
            {
                var rvb = t.Result;
                if (rvb is null) return;
                sessionCache.RearHsrPercentage = rvb.HighSpeedRebound;
                sessionCache.RearLsrPercentage = rvb.LowSpeedRebound;
                sessionCache.RearLscPercentage = rvb.LowSpeedCompression;
                sessionCache.RearHscPercentage = rvb.HighSpeedCompression;
                Dispatcher.UIThread.Post(() =>
                {
                    DamperPage.RearHsrPercentage = rvb.HighSpeedRebound;
                    DamperPage.RearLsrPercentage = rvb.LowSpeedRebound;
                    DamperPage.RearLscPercentage = rvb.LowSpeedCompression;
                    DamperPage.RearHscPercentage = rvb.HighSpeedCompression;
                });
            }, TaskScheduler.Default));
        }

        if (telemetryData.Front.Present && telemetryData.Rear.Present)
        {
            EnqueueSlotGroup(tasks, context, sessionCache, viewModel, throttledPlotTask, PlotGroup.Balance);
        }
        else
        {
            Dispatcher.UIThread.Post(() => { Pages.Remove(BalancePage); });
        }

        EnqueueSlotGroup(tasks, context, sessionCache, viewModel, throttledPlotTask, PlotGroup.TimeCropped);

        // Combined Front+Rear FFT and Balance metrics — only when both sides are present.
        if (telemetryData.Front.Present && telemetryData.Rear.Present)
        {
            var discipline = await disciplineTask!;
            var (peakMinHz, peakMaxHz) = TelemetryData.BodyResonancePeakBandFor(discipline);
            var fftContext = context with
            {
                Late = new PlotLateContext(discipline, peakMinHz, peakMaxHz, null)
            };

            EnqueueSlotGroup(tasks, fftContext, sessionCache, viewModel, throttledPlotTask, PlotGroup.Fft);

            tasks.Add(Task.Run(async () =>
            {
                // Refresh Wheelbase from the live linkage row — the Linkage carried
                // in telemetryData comes from the MessagePack blob, whose Id is
                // [IgnoreMember] and is regenerated on deserialization. Resolve the
                // real linkage via session.Setup → Setup.LinkageId.
                if (telemetryData.Linkage is { } lk && lk.Wheelbase is null or 0 && session.Setup.HasValue)
                {
                    var setup = await databaseService.GetSetupAsync(session.Setup.Value);
                    if (setup is not null)
                    {
                        var liveLinkage = await databaseService.GetLinkageAsync(setup.LinkageId);
                        if (liveLinkage?.Wheelbase is > 0) lk.Wheelbase = liveLinkage.Wheelbase;
                    }
                }
                var metrics = telemetryData.CalculateBalanceMetrics(discipline);
                sessionCache.BalanceMetricsJson = JsonSerializer.Serialize(metrics);
                var balanceOverrides = await getBalanceOverridesAsync(discipline);
                Dispatcher.UIThread.Post(() => BalancePage.Metrics.Apply(metrics, discipline, balanceOverrides));

                // Pitch-attitude plots (lag-corrected). The expected band comes from the effective
                // SAG green ranges so the plot's reference matches the μ metric's traffic light.
                // Wheelbase was refreshed just above, so CalculatePitchDegrees sees it.
                var expectedBand = BalanceTargetDefaults.ExpectedPitchBand(
                    BalanceTargetDefaults.EffectiveGreen(balanceOverrides, "FrontSag", discipline),
                    BalanceTargetDefaults.EffectiveRearSagBand(balanceOverrides, discipline,
                        metrics.MaxRearStrokeMm, metrics.ShockWheelCoeffs, metrics.MaxRearTravelMm),
                    metrics.MaxFrontTravelMm, metrics.MaxRearTravelMm, metrics.WheelbaseMm);
                // Band signature for LoadCache's staleness check — the band is baked into the
                // PitchBalance SVG below, so a later per-discipline override edit must be able
                // to invalidate this cache row.
                sessionCache.PitchExpectedMinDeg = expectedBand?.minDeg;
                sessionCache.PitchExpectedMaxDeg = expectedBand?.maxDeg;

                var pitchContext = fftContext with
                {
                    Late = new PlotLateContext(discipline, peakMinHz, peakMaxHz, expectedBand)
                };
                await Task.WhenAll(
                    RenderSlot(SessionPlotCatalog.ByLabel("pitchBalance"), pitchContext, sessionCache, viewModel, throttledPlotTask),
                    RenderSlot(SessionPlotCatalog.ByLabel("pitchCoherence"), pitchContext, sessionCache, viewModel, throttledPlotTask),
                    RenderSlot(SessionPlotCatalog.ByLabel("goutScatter"), pitchContext, sessionCache, viewModel, throttledPlotTask));
            }));
        }

        EnqueueSlotGroup(tasks, context, sessionCache, viewModel, throttledPlotTask, PlotGroup.Misc);

        // Combined sessions skip all three phase-portrait plots: cache columns stay null and
        // MiscPageView hides the corresponding images via its IsVisible bindings.
        if (isCombined)
        {
            Dispatcher.UIThread.Post(() =>
            {
                MiscPage.PositionVelocityComparison = null;
                MiscPage.FrontPositionVelocity = null;
                MiscPage.RearPositionVelocity = null;
            });
        }
        else
        {
            EnqueueSlotGroup(tasks, context, sessionCache, viewModel, throttledPlotTask, PlotGroup.PhasePortrait);
        }

        // Summary runs concurrently with all plots (reuses shared VelocityBands tasks)
        tasks.Add(Task.Run(async () =>
        {
            var summaryData = await populateSummaryAsync(telemetryData, frontBandsTask, rearBandsTask);
            sessionCache.SummaryJson = JsonSerializer.Serialize(summaryData);
        }));

        await Task.WhenAll(tasks);

        await databaseService.PutSessionCacheAsync(sessionCache);
        PerfLog.Log("cache/total", swCache.Elapsed.TotalMilliseconds);
    }

    private static void EnqueueSlotGroup(
        List<Task> tasks,
        PlotContext context,
        SessionCache sessionCache,
        SessionViewModel viewModel,
        Func<string, Action, Task> throttledPlotTask,
        PlotGroup group)
    {
        foreach (var slot in SessionPlotCatalog.InGroup(group))
        {
            if (slot.Applies(context))
            {
                tasks.Add(RenderSlot(slot, context, sessionCache, viewModel, throttledPlotTask));
            }
        }
    }

    private static Task RenderSlot(
        PlotSlot slot,
        PlotContext context,
        SessionCache sessionCache,
        SessionViewModel viewModel,
        Func<string, Action, Task> throttledPlotTask)
    {
        return throttledPlotTask(slot.Label, () =>
        {
            var plot = slot.Create(context);
            ((TelemetryPlot)plot).LoadTelemetryData(slot.Source(context));
            slot.PrepareRender?.Invoke(plot);
            var (width, height) = ResolveSize(slot.Size, context);
            slot.StoreSvg(sessionCache, plot.Plot.GetSvgXml(width, height));
            var source = SvgToSource(slot.ReadSvg(sessionCache));
            Dispatcher.UIThread.Post(() => slot.Assign(viewModel, SourceToImage(source)));
        });
    }

    private static (int width, int height) ResolveSize(PlotSize size, PlotContext context) => size switch
    {
        PlotSize.Standard => (context.Width, context.Height),
        PlotSize.LessBandView => (context.Width, context.Height - SessionViewModel.VelocityBandViewHeight),
        PlotSize.TravelTimeHistory => (context.Width, context.TthHeight),
        _ => throw new ArgumentOutOfRangeException(nameof(size), size, null)
    };
}
