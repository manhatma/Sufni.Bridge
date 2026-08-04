using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Sufni.Bridge.Models;
using Sufni.Bridge.Models.Telemetry;
using Sufni.Bridge.Services;
using Sufni.Bridge.ViewModels.SessionPages;
using static Sufni.Bridge.Extensions.SvgHelpers;

namespace Sufni.Bridge.ViewModels.Items;

internal static class SessionCacheLoader
{
    internal static async Task<(bool found, bool hasVdc, bool hasPvc)> LoadAsync(
        SessionViewModel viewModel,
        SessionCacheMeta? meta,
        Func<SessionCacheMeta?, Task<bool>> isCacheMetaCurrentAsync,
        Func<Task<Discipline?>> getSessionDisciplineAsync,
        Func<Discipline?, Task<Dictionary<string, (double? min, double? max)>?>> getBalanceOverridesAsync,
        Action<Task> setBackgroundSvgParse)
    {
        var Id = viewModel.Id;
        var DamperPage = viewModel.DamperPage;
        var BalancePage = viewModel.BalancePage;
        var SummaryPage = viewModel.SummaryPage;
        var Pages = viewModel.Pages;

        var databaseService = App.Current?.Services?.GetService<IDatabaseService>();
        Debug.Assert(databaseService != null, nameof(databaseService) + " != null");

        Debug.WriteLine($"Session {Id}: LoadCache - cache found={meta is not null}");
        if (!await isCacheMetaCurrentAsync(meta))
        {
            return (false, false, false);
        }

        SessionCache? cache;
        using (PerfLog.Measure("load/cacheRow"))
        {
            cache = await databaseService.GetSessionCacheAsync(Id);
        }
        // Row vanished between the meta probe and the wide fetch (e.g. invalidated by a
        // concurrent setup reassignment) — treat as stale.
        if (cache is null)
        {
            return (false, false, false);
        }

        var swParse = Stopwatch.StartNew();

        // Load TravelTimeHistory (full data, always in cache)
        var tthSlot = SessionPlotCatalog.ByLabel("tth");
        if (tthSlot.ReadSvg(cache) is not null)
        {
            var tthSrc = await Task.Run(() => SvgToSource(tthSlot.ReadSvg(cache)));
            tthSlot.Assign(viewModel, SourceToImage(tthSrc));
        }

        var velDistCompSlot = SessionPlotCatalog.ByLabel("velDistComp");
        var hasVdc = velDistCompSlot.ReadSvg(cache) is not null;

        // Combined sessions never get the phase-portrait plots cached (CreateCache skips them
        // and leaves the columns null by design) — treat that as "complete" rather than stale,
        // or the cache would never be considered valid and CreateCache would rerun on every open.
        var isCombined = (await databaseService.GetCombinedSourcesAsync(Id)).Count > 0;
        var posVelCompSlot = SessionPlotCatalog.ByLabel("posVelComp");
        var hasPvc = isCombined || posVelCompSlot.ReadSvg(cache) is not null;

        // 1. Summary: pure JSON, no SVG parsing — populate immediately
        if (cache.SummaryJson is not null)
        {
            try
            {
                var summary = JsonSerializer.Deserialize<SessionViewModel.CachedSummaryData>(cache.SummaryJson);
                if (summary is not null)
                {
                    SummaryPage.RunDataRows = new ObservableCollection<SummaryValueRow>(
                        summary.RunDataRows.Select(r => new SummaryValueRow(r[0], r[1])));
                    SummaryPage.ForkShockRows = new ObservableCollection<SummaryComparisonRow>(
                        summary.ForkShockRows.Select(r => new SummaryComparisonRow(r[0], r[1], r[2])));
                    SummaryPage.WheelRows = new ObservableCollection<SummaryComparisonRow>(
                        summary.WheelRows.Select(r => new SummaryComparisonRow(r[0], r[1], r[2])));
                    SummaryPage.Airtime = summary.Airtime;
                    SummaryPage.DataQuality = summary.DataQuality;
                }
            }
            catch
            {
                // Ignore corrupt summary cache - will be rebuilt from DB
            }
        }

        // 2. SpringPage SVGs: parse in parallel and await — first page with plots the user will see
        var travelCompSlot = SessionPlotCatalog.ByLabel("travelCompHist");
        var frontRearScatterSlot = SessionPlotCatalog.ByLabel("frontRearScatter");
        var frontTravelHistSlot = SessionPlotCatalog.ByLabel("frontTravelHist");
        var rearTravelHistSlot = SessionPlotCatalog.ByLabel("rearTravelHist");
        var travelCompTask       = Task.Run(() => SvgToSource(travelCompSlot.ReadSvg(cache)));
        var frontRearScatterTask = Task.Run(() => SvgToSource(frontRearScatterSlot.ReadSvg(cache)));
        var frontTravelHistTask  = Task.Run(() => SvgToSource(frontTravelHistSlot.ReadSvg(cache)));
        var rearTravelHistTask   = Task.Run(() => SvgToSource(rearTravelHistSlot.ReadSvg(cache)));

        await Task.WhenAll(travelCompTask, frontRearScatterTask, frontTravelHistTask, rearTravelHistTask);

        // SvgImage requires UI thread — Loaded command always runs on UI thread
        travelCompSlot.Assign(viewModel, SourceToImage(travelCompTask.Result));
        frontRearScatterSlot.Assign(viewModel, SourceToImage(frontRearScatterTask.Result));
        frontTravelHistSlot.Assign(viewModel, SourceToImage(frontTravelHistTask.Result));
        rearTravelHistSlot.Assign(viewModel, SourceToImage(rearTravelHistTask.Result));

        PerfLog.Log("load/springSvg", swParse.Elapsed.TotalMilliseconds);

        // 3. Remaining pages: parse in background, only when cache is complete.
        //    Incomplete caches have hasVdc/hasPvc=false → caller triggers CreateCache() instead.
        if (hasVdc && hasPvc)
        {
            setBackgroundSvgParse(Task.Run(async () =>
            {
                var swBg = Stopwatch.StartNew();
                var frontVelHistSlot = SessionPlotCatalog.ByLabel("frontVelHist");
                var frontLsVelHistSlot = SessionPlotCatalog.ByLabel("frontLsVelHist");
                var rearVelHistSlot = SessionPlotCatalog.ByLabel("rearVelHist");
                var rearDamperVelHistSlot = SessionPlotCatalog.ByLabel("rearDamperVelHist");
                var rearLsVelHistSlot = SessionPlotCatalog.ByLabel("rearLsVelHist");
                var combBalSlot = SessionPlotCatalog.ByLabel("combinedBalance");
                var compBalSlot = SessionPlotCatalog.ByLabel("compressionBalance");
                var rebBalSlot = SessionPlotCatalog.ByLabel("reboundBalance");
                var frontPosVelSlot = SessionPlotCatalog.ByLabel("frontPosVel");
                var rearPosVelSlot = SessionPlotCatalog.ByLabel("rearPosVel");
                var frontTravelCropSlot = SessionPlotCatalog.ByLabel("frontTravelTimeCropped");
                var rearTravelCropSlot = SessionPlotCatalog.ByLabel("rearTravelTimeCropped");
                var frontVelCropSlot = SessionPlotCatalog.ByLabel("frontVelTimeCropped");
                var rearVelCropSlot = SessionPlotCatalog.ByLabel("rearVelTimeCropped");
                var frontAccelCropSlot = SessionPlotCatalog.ByLabel("frontAccel");
                var rearAccelCropSlot = SessionPlotCatalog.ByLabel("rearAccel");
                var combinedFftSlot = SessionPlotCatalog.ByLabel("travelFft");
                var combinedFftHighSlot = SessionPlotCatalog.ByLabel("travelFftHigh");
                var combinedVelFftSlot = SessionPlotCatalog.ByLabel("velFft");
                var pitchBalanceSlot = SessionPlotCatalog.ByLabel("pitchBalance");
                var pitchCoherenceSlot = SessionPlotCatalog.ByLabel("pitchCoherence");
                var goutScatterSlot = SessionPlotCatalog.ByLabel("goutScatter");
                var cumulativeTravelSlot = SessionPlotCatalog.ByLabel("cumulativeTravel");

                var frontVelHistTask   = Task.Run(() => SvgToSource(frontVelHistSlot.ReadSvg(cache)));
                var frontLsVelHistTask = Task.Run(() => SvgToSource(frontLsVelHistSlot.ReadSvg(cache)));
                var rearVelHistTask    = Task.Run(() => SvgToSource(rearVelHistSlot.ReadSvg(cache)));
                var rearDamperVelHistTask = Task.Run(() => SvgToSource(rearDamperVelHistSlot.ReadSvg(cache)));
                var rearLsVelHistTask  = Task.Run(() => SvgToSource(rearLsVelHistSlot.ReadSvg(cache)));
                var combBalTask      = Task.Run(() => SvgToSource(combBalSlot.ReadSvg(cache)));
                var compBalTask      = Task.Run(() => SvgToSource(compBalSlot.ReadSvg(cache)));
                var rebBalTask       = Task.Run(() => SvgToSource(rebBalSlot.ReadSvg(cache)));
                var velDistCompTask  = Task.Run(() => SvgToSource(velDistCompSlot.ReadSvg(cache)));
                var posVelCompTask   = Task.Run(() => SvgToSource(posVelCompSlot.ReadSvg(cache)));
                var frontPosVelTask  = Task.Run(() => SvgToSource(frontPosVelSlot.ReadSvg(cache)));
                var rearPosVelTask   = Task.Run(() => SvgToSource(rearPosVelSlot.ReadSvg(cache)));
                var frontTravelCropTask = Task.Run(() => SvgToSource(frontTravelCropSlot.ReadSvg(cache)));
                var rearTravelCropTask  = Task.Run(() => SvgToSource(rearTravelCropSlot.ReadSvg(cache)));
                var frontVelCropTask    = Task.Run(() => SvgToSource(frontVelCropSlot.ReadSvg(cache)));
                var rearVelCropTask     = Task.Run(() => SvgToSource(rearVelCropSlot.ReadSvg(cache)));
                var frontAccelCropTask  = Task.Run(() => SvgToSource(frontAccelCropSlot.ReadSvg(cache)));
                var rearAccelCropTask   = Task.Run(() => SvgToSource(rearAccelCropSlot.ReadSvg(cache)));
                var combinedFftTask     = Task.Run(() => SvgToSource(combinedFftSlot.ReadSvg(cache)));
                var combinedFftHighTask = Task.Run(() => SvgToSource(combinedFftHighSlot.ReadSvg(cache)));
                var combinedVelFftTask  = Task.Run(() => SvgToSource(combinedVelFftSlot.ReadSvg(cache)));
                var pitchBalanceTask    = Task.Run(() => SvgToSource(pitchBalanceSlot.ReadSvg(cache)));
                var pitchCoherenceTask  = Task.Run(() => SvgToSource(pitchCoherenceSlot.ReadSvg(cache)));
                var goutScatterTask     = Task.Run(() => SvgToSource(goutScatterSlot.ReadSvg(cache)));
                var cumulativeTravelTask = Task.Run(() => SvgToSource(cumulativeTravelSlot.ReadSvg(cache)));

                await Task.WhenAll(frontVelHistTask, frontLsVelHistTask, rearVelHistTask, rearDamperVelHistTask, rearLsVelHistTask,
                    combBalTask, compBalTask, rebBalTask,
                    velDistCompTask, posVelCompTask, frontPosVelTask, rearPosVelTask,
                    frontTravelCropTask, rearTravelCropTask, frontVelCropTask, rearVelCropTask,
                    frontAccelCropTask, rearAccelCropTask,
                    combinedFftTask, combinedFftHighTask,
                    pitchBalanceTask, pitchCoherenceTask, goutScatterTask, cumulativeTravelTask);

                PerfLog.Log("load/bgSvg", swBg.Elapsed.TotalMilliseconds);

                var frontVelHistSrc   = frontVelHistTask.Result;
                var frontLsVelHistSrc = frontLsVelHistTask.Result;
                var rearVelHistSrc    = rearVelHistTask.Result;
                var rearDamperVelHistSrc = rearDamperVelHistTask.Result;
                var rearLsVelHistSrc  = rearLsVelHistTask.Result;
                var combBalSrc      = combBalTask.Result;
                var compBalSrc      = compBalTask.Result;
                var rebBalSrc       = rebBalTask.Result;
                var velDistCompSrc  = velDistCompTask.Result;
                var posVelCompSrc   = posVelCompTask.Result;
                var frontPosVelSrc  = frontPosVelTask.Result;
                var rearPosVelSrc   = rearPosVelTask.Result;

                // Resolve discipline up-front (async DB lookup) — the UI-thread
                // lambda below is sync and can't await.
                var sessionDiscipline = cache.BalanceMetricsJson is not null
                    ? await getSessionDisciplineAsync()
                    : null;
                var balanceOverrides = await getBalanceOverridesAsync(sessionDiscipline);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    frontVelHistSlot.Assign(viewModel, SourceToImage(frontVelHistSrc));
                    frontLsVelHistSlot.Assign(viewModel, SourceToImage(frontLsVelHistSrc));
                    rearVelHistSlot.Assign(viewModel, SourceToImage(rearVelHistSrc));
                    rearDamperVelHistSlot.Assign(viewModel, SourceToImage(rearDamperVelHistSrc));
                    rearLsVelHistSlot.Assign(viewModel, SourceToImage(rearLsVelHistSrc));
                    DamperPage.FrontHscPercentage     = cache.FrontHscPercentage;
                    DamperPage.RearHscPercentage      = cache.RearHscPercentage;
                    DamperPage.FrontLscPercentage     = cache.FrontLscPercentage;
                    DamperPage.RearLscPercentage      = cache.RearLscPercentage;
                    DamperPage.FrontLsrPercentage     = cache.FrontLsrPercentage;
                    DamperPage.RearLsrPercentage      = cache.RearLsrPercentage;
                    DamperPage.FrontHsrPercentage     = cache.FrontHsrPercentage;
                    DamperPage.RearHsrPercentage      = cache.RearHsrPercentage;

                    if (compBalSrc is not null)
                    {
                        combBalSlot.Assign(viewModel, SourceToImage(combBalSrc));
                        compBalSlot.Assign(viewModel, SourceToImage(compBalSrc));
                        rebBalSlot.Assign(viewModel, SourceToImage(rebBalSrc));
                        combinedFftSlot.Assign(viewModel, SourceToImage(combinedFftTask.Result));
                        combinedFftHighSlot.Assign(viewModel, SourceToImage(combinedFftHighTask.Result));
                        combinedVelFftSlot.Assign(viewModel, SourceToImage(combinedVelFftTask.Result));
                        pitchBalanceSlot.Assign(viewModel, SourceToImage(pitchBalanceTask.Result));
                        pitchCoherenceSlot.Assign(viewModel, SourceToImage(pitchCoherenceTask.Result));
                        goutScatterSlot.Assign(viewModel, SourceToImage(goutScatterTask.Result));
                        cumulativeTravelSlot.Assign(viewModel, SourceToImage(cumulativeTravelTask.Result));
                        if (cache.BalanceMetricsJson is not null)
                        {
                            try
                            {
                                var m = JsonSerializer.Deserialize<BalanceMetrics>(cache.BalanceMetricsJson);
                                if (m is not null) BalancePage.Metrics.Apply(m, sessionDiscipline, balanceOverrides);
                            }
                            catch { /* corrupt metrics cache; will be rebuilt */ }
                        }
                    }
                    else
                    {
                        Pages.Remove(BalancePage);
                    }

                    velDistCompSlot.Assign(viewModel, SourceToImage(velDistCompSrc));
                    frontTravelCropSlot.Assign(viewModel, SourceToImage(frontTravelCropTask.Result));
                    rearTravelCropSlot.Assign(viewModel, SourceToImage(rearTravelCropTask.Result));
                    frontVelCropSlot.Assign(viewModel, SourceToImage(frontVelCropTask.Result));
                    rearVelCropSlot.Assign(viewModel, SourceToImage(rearVelCropTask.Result));
                    posVelCompSlot.Assign(viewModel, SourceToImage(posVelCompSrc));
                    frontPosVelSlot.Assign(viewModel, SourceToImage(frontPosVelSrc));
                    rearPosVelSlot.Assign(viewModel, SourceToImage(rearPosVelSrc));
                    frontAccelCropSlot.Assign(viewModel, SourceToImage(frontAccelCropTask.Result));
                    rearAccelCropSlot.Assign(viewModel, SourceToImage(rearAccelCropTask.Result));
                });
            }));
        }

        return (true, hasVdc, hasPvc);
    }
}
