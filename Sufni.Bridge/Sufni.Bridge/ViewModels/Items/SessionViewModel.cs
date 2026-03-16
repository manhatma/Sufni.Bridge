using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using ScottPlot;
using Sufni.Bridge.Models;
using Sufni.Bridge.Models.Telemetry;
using Sufni.Bridge.Plots;
using Sufni.Bridge.Services;
using Sufni.Bridge.ViewModels.SessionPages;

namespace Sufni.Bridge.ViewModels.Items;

public partial class SessionViewModel : ItemViewModelBase
{
    private Session session;
    public bool IsInDatabase;
    private SpringPageViewModel SpringPage { get; } = new();
    private DamperPageViewModel DamperPage { get; } = new();
    private BalancePageViewModel BalancePage { get; } = new();
    private MiscPageViewModel MiscPage { get; } = new();
    private SummaryPageViewModel SummaryPage { get; } = new();
    private NotesPageViewModel NotesPage { get; } = new();
    public ObservableCollection<PageViewModelBase> Pages { get; }
    public string Description => NotesPage.Description ?? "";
    public override bool IsComplete => session.HasProcessedData;

    #region Private methods

    private async Task<bool> LoadCache()
    {
        var databaseService = App.Current?.Services?.GetService<IDatabaseService>();
        Debug.Assert(databaseService != null, nameof(databaseService) + " != null");

        var cache = await databaseService.GetSessionCacheAsync(Id);
        if (cache is null)
        {
            return false;
        }

        SpringPage.TravelComparisonHistogram = cache.TravelComparisonHistogram;
        SpringPage.FrontRearTravelScatter = cache.FrontRearTravelScatter;
        SpringPage.FrontTravelHistogram = cache.FrontTravelHistogram;
        SpringPage.RearTravelHistogram = cache.RearTravelHistogram;

        DamperPage.FrontVelocityHistogram = cache.FrontVelocityHistogram;
        DamperPage.RearVelocityHistogram = cache.RearVelocityHistogram;
        DamperPage.FrontHscPercentage = cache.FrontHscPercentage;
        DamperPage.RearHscPercentage = cache.RearHscPercentage;
        DamperPage.FrontLscPercentage = cache.FrontLscPercentage;
        DamperPage.RearLscPercentage = cache.RearLscPercentage;
        DamperPage.FrontLsrPercentage = cache.FrontLsrPercentage;
        DamperPage.RearLsrPercentage = cache.RearLsrPercentage;
        DamperPage.FrontHsrPercentage = cache.FrontHsrPercentage;
        DamperPage.RearHsrPercentage = cache.RearHsrPercentage;

        if (cache.CompressionBalance is not null)
        {
            BalancePage.CompressionBalance = cache.CompressionBalance;
            BalancePage.ReboundBalance = cache.ReboundBalance;
        }
        else
        {
            Pages.Remove(BalancePage);
        }

        MiscPage.VelocityDistributionComparison = cache.VelocityDistributionComparison;
        MiscPage.PositionVelocityComparison = cache.PositionVelocityComparison;
        MiscPage.FrontPositionVelocity = cache.FrontPositionVelocity;
        MiscPage.RearPositionVelocity = cache.RearPositionVelocity;

        if (cache.SummaryJson is not null)
        {
            var summaryData = JsonSerializer.Deserialize<SummaryCacheData>(cache.SummaryJson);
            if (summaryData is not null)
                ApplySummaryData(summaryData);
        }

        return true;
    }

    private async Task CreateCache(object? bounds)
    {
        var databaseService = App.Current?.Services?.GetService<IDatabaseService>();
        Debug.Assert(databaseService != null, nameof(databaseService) + " != null");

        var telemetryData = await databaseService.GetSessionPsstAsync(Id);
        if (telemetryData is null)
        {
            throw new Exception("Database error");
        }

        var b = (Rect)bounds!;
        var (width, height) = ((int)b.Width, (int)(b.Height / 2.0));
        var sessionCache = new SessionCache
        {
            SessionId = Id
        };

        if (telemetryData.Front.Present && telemetryData.Rear.Present)
        {
            var tcmp = new TravelHistogramComparisonPlot(new Plot());
            tcmp.LoadTelemetryData(telemetryData);
            sessionCache.TravelComparisonHistogram = tcmp.Plot.GetSvgXml(width, height);
            Dispatcher.UIThread.Post(() =>
            {
                SpringPage.TravelComparisonHistogram = sessionCache.TravelComparisonHistogram;
            });

            var frs = new FrontRearTravelScatterPlot(new Plot());
            frs.LoadTelemetryData(telemetryData);
            sessionCache.FrontRearTravelScatter = frs.Plot.GetSvgXml(width, height);
            Dispatcher.UIThread.Post(() =>
            {
                SpringPage.FrontRearTravelScatter = sessionCache.FrontRearTravelScatter;
            });
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                SpringPage.TravelComparisonHistogram = null;
                SpringPage.FrontRearTravelScatter = null;
            });
        }

        if (telemetryData.Front.Present)
        {
            var fth = new TravelHistogramPlot(new Plot(), SuspensionType.Front);
            fth.LoadTelemetryData(telemetryData);
            sessionCache.FrontTravelHistogram = fth.Plot.GetSvgXml(width, height);
            Dispatcher.UIThread.Post(() => { SpringPage.FrontTravelHistogram = sessionCache.FrontTravelHistogram; });

            var fvh = new VelocityHistogramPlot(new Plot(), SuspensionType.Front);
            fvh.LoadTelemetryData(telemetryData);
            sessionCache.FrontVelocityHistogram = fvh.Plot.GetSvgXml(width - 64, 478);
            Dispatcher.UIThread.Post(() => { DamperPage.FrontVelocityHistogram = sessionCache.FrontVelocityHistogram; });

            var fvb = telemetryData.CalculateVelocityBands(SuspensionType.Front, 200);
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
        }

        if (telemetryData.Rear.Present)
        {
            var rth = new TravelHistogramPlot(new Plot(), SuspensionType.Rear);
            rth.LoadTelemetryData(telemetryData);
            sessionCache.RearTravelHistogram = rth.Plot.GetSvgXml(width, height);
            Dispatcher.UIThread.Post(() => { SpringPage.RearTravelHistogram = sessionCache.RearTravelHistogram; });

            var rvh = new VelocityHistogramPlot(new Plot(), SuspensionType.Rear);
            rvh.LoadTelemetryData(telemetryData);
            sessionCache.RearVelocityHistogram = rvh.Plot.GetSvgXml(width - 64, 478);
            Dispatcher.UIThread.Post(() => { DamperPage.RearVelocityHistogram = sessionCache.RearVelocityHistogram; });

            var rvb = telemetryData.CalculateVelocityBands(SuspensionType.Rear, 200);
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
        }

        if (telemetryData.Front.Present && telemetryData.Rear.Present)
        {

            var cb = new BalancePlot(new Plot(), BalanceType.Compression);
            cb.LoadTelemetryData(telemetryData);
            sessionCache.CompressionBalance = cb.Plot.GetSvgXml(width - 40, height);
            Dispatcher.UIThread.Post(() => { BalancePage.CompressionBalance = sessionCache.CompressionBalance; });

            var rb = new BalancePlot(new Plot(), BalanceType.Rebound);
            rb.LoadTelemetryData(telemetryData);
            sessionCache.ReboundBalance = rb.Plot.GetSvgXml(width - 40, height);
            Dispatcher.UIThread.Post(() => { BalancePage.ReboundBalance = sessionCache.ReboundBalance; });
        }
        else
        {
            Dispatcher.UIThread.Post(() => { Pages.Remove(BalancePage); });
        }

        // BYB diagrams
        var vdc = new VelocityDistributionComparisonPlot(new Plot());
        vdc.LoadTelemetryData(telemetryData);
        sessionCache.VelocityDistributionComparison = vdc.Plot.GetSvgXml(width - 40, height);
        Dispatcher.UIThread.Post(() => { MiscPage.VelocityDistributionComparison = sessionCache.VelocityDistributionComparison; });

        var pvc = new PositionVelocityComparisonPlot(new Plot());
        pvc.LoadTelemetryData(telemetryData);
        sessionCache.PositionVelocityComparison = pvc.Plot.GetSvgXml(width, height);
        Dispatcher.UIThread.Post(() => { MiscPage.PositionVelocityComparison = sessionCache.PositionVelocityComparison; });

        if (telemetryData.Front.Present)
        {
            var fpv = new PositionVelocityPlot(new Plot(), SuspensionType.Front);
            fpv.LoadTelemetryData(telemetryData);
            sessionCache.FrontPositionVelocity = fpv.Plot.GetSvgXml(width, height);
            Dispatcher.UIThread.Post(() => { MiscPage.FrontPositionVelocity = sessionCache.FrontPositionVelocity; });
        }

        if (telemetryData.Rear.Present)
        {
            var rpv = new PositionVelocityPlot(new Plot(), SuspensionType.Rear);
            rpv.LoadTelemetryData(telemetryData);
            sessionCache.RearPositionVelocity = rpv.Plot.GetSvgXml(width, height);
            Dispatcher.UIThread.Post(() => { MiscPage.RearPositionVelocity = sessionCache.RearPositionVelocity; });
        }

        var summaryData = BuildSummaryData(telemetryData);
        sessionCache.SummaryJson = JsonSerializer.Serialize(summaryData);
        ApplySummaryData(summaryData);

        await databaseService.PutSessionCacheAsync(sessionCache);
    }

    private sealed record SuspensionSummaryStats(
        double MaxTravel,
        double AvgTravel,
        int Bottomouts,
        double AvgCompression,
        double MaxCompression,
        double AvgRebound,
        double MaxRebound);

    private static string FormatTravel(double value, double maxTravel)
    {
        if (maxTravel <= 0)
        {
            return "-";
        }

        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{value / maxTravel * 100.0:0.0} % - {value:0.0} mm");
    }

    private static string FormatVelocity(double value)
    {
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{value:0.0} mm/s");
    }

    private static string FormatBottomouts(int value) => $"{value} times";

    private static double EvaluatePolynomial(IReadOnlyList<double> coefficients, double x)
    {
        var result = 0.0;
        var power = 1.0;
        for (var i = 0; i < coefficients.Count; i++)
        {
            result += coefficients[i] * power;
            power *= x;
        }
        return result;
    }

    private static double EvaluateDerivative(IReadOnlyList<double> coefficients, double x)
    {
        var result = 0.0;
        var power = 1.0;
        for (var i = 1; i < coefficients.Count; i++)
        {
            result += i * coefficients[i] * power;
            power *= x;
        }
        return result;
    }

    private static double SolveShockTravel(double wheelTravel, IReadOnlyList<double> coefficients, double maxShockStroke)
    {
        if (wheelTravel <= 0)
        {
            return 0.0;
        }

        var maxWheelTravel = EvaluatePolynomial(coefficients, maxShockStroke);
        var x = maxWheelTravel > 0 ? wheelTravel / maxWheelTravel * maxShockStroke : 0.0;
        x = Math.Clamp(x, 0.0, maxShockStroke);

        for (var i = 0; i < 12; i++)
        {
            var f = EvaluatePolynomial(coefficients, x) - wheelTravel;
            if (Math.Abs(f) < 1e-6)
            {
                break;
            }

            var df = EvaluateDerivative(coefficients, x);
            if (Math.Abs(df) < 1e-6)
            {
                break;
            }

            x = Math.Clamp(x - f / df, 0.0, maxShockStroke);
        }

        return x;
    }

    private static SuspensionSummaryStats? BuildWheelStats(TelemetryData telemetryData, SuspensionType type)
    {
        var suspension = type == SuspensionType.Front ? telemetryData.Front : telemetryData.Rear;
        if (!suspension.Present)
        {
            return null;
        }

        var travelStats = telemetryData.CalculateTravelStatistics(type);
        var velocityStats = telemetryData.CalculateVelocityStatistics(type);
        return new SuspensionSummaryStats(
            travelStats.Max,
            travelStats.Average,
            travelStats.Bottomouts,
            velocityStats.AverageCompression,
            velocityStats.MaxCompression,
            velocityStats.AverageRebound,
            velocityStats.MaxRebound);
    }

    private static SuspensionSummaryStats? BuildShockStats(TelemetryData telemetryData)
    {
        if (!telemetryData.Rear.Present || !telemetryData.Linkage.MaxRearStroke.HasValue || telemetryData.Linkage.MaxRearStroke <= 0)
        {
            return null;
        }

        var maxShockStroke = telemetryData.Linkage.MaxRearStroke.Value;
        var coeffs = telemetryData.Linkage.ShockWheelCoeffs;
        var rearTravel = telemetryData.Rear.Travel;
        var rearVelocity = telemetryData.Rear.Velocity;
        var shockTravel = new double[rearTravel.Length];
        var shockVelocity = new double[rearVelocity.Length];

        for (var i = 0; i < rearTravel.Length; i++)
        {
            var s = SolveShockTravel(rearTravel[i], coeffs, maxShockStroke);
            shockTravel[i] = s;
            var derivative = EvaluateDerivative(coeffs, s);
            shockVelocity[i] = Math.Abs(derivative) > 1e-6 ? rearVelocity[i] / derivative : 0.0;
        }

        double travelSum = 0.0;
        var travelCount = 0;
        double travelMax = 0.0;
        double compressionSum = 0.0;
        var compressionCount = 0;
        double compressionMax = 0.0;
        double reboundSum = 0.0;
        var reboundCount = 0;
        double reboundMax = 0.0;

        foreach (var stroke in telemetryData.Rear.Strokes.Compressions.Concat(telemetryData.Rear.Strokes.Rebounds))
        {
            for (var i = stroke.Start; i <= stroke.End && i < shockTravel.Length; i++)
            {
                var t = shockTravel[i];
                travelSum += t;
                travelCount++;
                if (t > travelMax)
                {
                    travelMax = t;
                }
            }
        }

        foreach (var stroke in telemetryData.Rear.Strokes.Compressions)
        {
            for (var i = stroke.Start; i <= stroke.End && i < shockVelocity.Length; i++)
            {
                var v = shockVelocity[i];
                compressionSum += v;
                compressionCount++;
                if (v > compressionMax)
                {
                    compressionMax = v;
                }
            }
        }

        foreach (var stroke in telemetryData.Rear.Strokes.Rebounds)
        {
            for (var i = stroke.Start; i <= stroke.End && i < shockVelocity.Length; i++)
            {
                var v = shockVelocity[i];
                reboundSum += v;
                reboundCount++;
                if (v < reboundMax)
                {
                    reboundMax = v;
                }
            }
        }

        var bottomouts = 0;
        var threshold = maxShockStroke * 0.97;
        for (var i = 0; i < shockTravel.Length; i++)
        {
            if (shockTravel[i] <= threshold)
            {
                continue;
            }

            bottomouts++;
            while (i < shockTravel.Length && shockTravel[i] > threshold)
            {
                i++;
            }
        }

        if (travelCount == 0)
        {
            return null;
        }

        return new SuspensionSummaryStats(
            travelMax,
            travelSum / travelCount,
            bottomouts,
            compressionCount > 0 ? compressionSum / compressionCount : 0.0,
            compressionMax,
            reboundCount > 0 ? reboundSum / reboundCount : 0.0,
            reboundMax);
    }

    private SummaryCacheData BuildSummaryData(TelemetryData telemetryData)
    {
        var date = (Timestamp ?? DateTime.UnixEpoch).ToString("yyyy-MM-dd");
        var time = (Timestamp ?? DateTime.UnixEpoch).ToString("HH:mm");
        var sampleCount = Math.Max(telemetryData.Front.Travel?.Length ?? 0, telemetryData.Rear.Travel?.Length ?? 0);
        var duration = telemetryData.SampleRate > 0
            ? TimeSpan.FromSeconds(sampleCount / (double)telemetryData.SampleRate)
            : TimeSpan.Zero;
        var runDuration = duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss")
            : duration.ToString(@"m\:ss");

        var runDataRows = new List<SummaryValueRow>
        {
            new("Date", date),
            new("Time", time),
            new("Run duration", $"{runDuration} s")
        };

        var forkStats = BuildWheelStats(telemetryData, SuspensionType.Front);
        var shockStats = BuildShockStats(telemetryData);
        var frontWheelStats = BuildWheelStats(telemetryData, SuspensionType.Front);
        var rearWheelStats = BuildWheelStats(telemetryData, SuspensionType.Rear);

        var forkShockRows = new List<SummaryComparisonRow>
        {
            new("Pos [AVG]",
                forkStats is null ? "-" : FormatTravel(forkStats.AvgTravel, telemetryData.Linkage.MaxFrontTravel),
                shockStats is null ? "-" : FormatTravel(shockStats.AvgTravel, telemetryData.Linkage.MaxRearStroke ?? 0)),
            new("Pos [MAX]",
                forkStats is null ? "-" : FormatTravel(forkStats.MaxTravel, telemetryData.Linkage.MaxFrontTravel),
                shockStats is null ? "-" : FormatTravel(shockStats.MaxTravel, telemetryData.Linkage.MaxRearStroke ?? 0)),
            new("Bottom out",
                forkStats is null ? "-" : FormatBottomouts(forkStats.Bottomouts),
                shockStats is null ? "-" : FormatBottomouts(shockStats.Bottomouts)),
            new("Comp [AVG]",
                forkStats is null ? "-" : FormatVelocity(forkStats.AvgCompression),
                shockStats is null ? "-" : FormatVelocity(shockStats.AvgCompression)),
            new("Comp [MAX]",
                forkStats is null ? "-" : FormatVelocity(forkStats.MaxCompression),
                shockStats is null ? "-" : FormatVelocity(shockStats.MaxCompression)),
            new("Reb [AVG]",
                forkStats is null ? "-" : FormatVelocity(forkStats.AvgRebound),
                shockStats is null ? "-" : FormatVelocity(shockStats.AvgRebound)),
            new("Reb [MAX]",
                forkStats is null ? "-" : FormatVelocity(forkStats.MaxRebound),
                shockStats is null ? "-" : FormatVelocity(shockStats.MaxRebound))
        };

        var wheelRows = new List<SummaryComparisonRow>
        {
            new("Pos [AVG]",
                frontWheelStats is null ? "-" : FormatTravel(frontWheelStats.AvgTravel, telemetryData.Linkage.MaxFrontTravel),
                rearWheelStats is null ? "-" : FormatTravel(rearWheelStats.AvgTravel, telemetryData.Linkage.MaxRearTravel)),
            new("Pos [MAX]",
                frontWheelStats is null ? "-" : FormatTravel(frontWheelStats.MaxTravel, telemetryData.Linkage.MaxFrontTravel),
                rearWheelStats is null ? "-" : FormatTravel(rearWheelStats.MaxTravel, telemetryData.Linkage.MaxRearTravel)),
            new("Bottom out",
                frontWheelStats is null ? "-" : FormatBottomouts(frontWheelStats.Bottomouts),
                rearWheelStats is null ? "-" : FormatBottomouts(rearWheelStats.Bottomouts)),
            new("Comp [AVG]",
                frontWheelStats is null ? "-" : FormatVelocity(frontWheelStats.AvgCompression),
                rearWheelStats is null ? "-" : FormatVelocity(rearWheelStats.AvgCompression)),
            new("Comp [MAX]",
                frontWheelStats is null ? "-" : FormatVelocity(frontWheelStats.MaxCompression),
                rearWheelStats is null ? "-" : FormatVelocity(rearWheelStats.MaxCompression)),
            new("Reb [AVG]",
                frontWheelStats is null ? "-" : FormatVelocity(frontWheelStats.AvgRebound),
                rearWheelStats is null ? "-" : FormatVelocity(rearWheelStats.AvgRebound)),
            new("Reb [MAX]",
                frontWheelStats is null ? "-" : FormatVelocity(frontWheelStats.MaxRebound),
                rearWheelStats is null ? "-" : FormatVelocity(rearWheelStats.MaxRebound))
        };

        return new SummaryCacheData(runDataRows, forkShockRows, wheelRows);
    }

    private void ApplySummaryData(SummaryCacheData data)
    {
        SummaryPage.RunDataRows = new ObservableCollection<SummaryValueRow>(data.RunDataRows);
        SummaryPage.ForkShockRows = new ObservableCollection<SummaryComparisonRow>(data.ForkShockRows);
        SummaryPage.WheelRows = new ObservableCollection<SummaryComparisonRow>(data.WheelRows);
    }

    #endregion

    #region Constructors

    public SessionViewModel()
    {
        session = new Session();
        IsInDatabase = false;
        Pages = [SummaryPage, SpringPage, DamperPage, BalancePage, MiscPage, NotesPage];
    }

    public SessionViewModel(Session session, bool fromDatabase)
    {
        this.session = session;
        IsInDatabase = fromDatabase;
        Pages = [SummaryPage, SpringPage, DamperPage, BalancePage, MiscPage, NotesPage];

        NotesPage.ForkSettings.PropertyChanged += (_, _) => EvaluateDirtiness();
        NotesPage.ShockSettings.PropertyChanged += (_, _) => EvaluateDirtiness();
        NotesPage.PropertyChanged += (_, _) => EvaluateDirtiness();

        ResetImplementation();
    }

    #endregion

    #region ItemViewModelBase overrides
    protected override void EvaluateDirtiness()
    {
        IsDirty =
            !IsInDatabase ||
            Name != session.Name ||
            NotesPage.IsDirty(session);
    }

    protected override async Task SaveImplementation()
    {
        var databaseService = App.Current?.Services?.GetService<IDatabaseService>();
        Debug.Assert(databaseService != null, nameof(databaseService) + " != null");

        try
        {
            var newSession = new Session(
                id: session.Id,
                name: Name ?? $"session #{session.Id}",
                description: NotesPage.Description ?? $"session #{session.Id}",
                setup: session.Setup)
            {
                FrontSpringRate = NotesPage.ForkSettings.SpringRate,
                FrontHighSpeedCompression = NotesPage.ForkSettings.HighSpeedCompression,
                FrontLowSpeedCompression = NotesPage.ForkSettings.LowSpeedCompression,
                FrontLowSpeedRebound = NotesPage.ForkSettings.LowSpeedRebound,
                FrontHighSpeedRebound = NotesPage.ForkSettings.HighSpeedRebound,
                FrontVolSpc = NotesPage.ForkSettings.VolSpc,
                RearSpringRate = NotesPage.ShockSettings.SpringRate,
                RearHighSpeedCompression = NotesPage.ShockSettings.HighSpeedCompression,
                RearLowSpeedCompression = NotesPage.ShockSettings.LowSpeedCompression,
                RearLowSpeedRebound = NotesPage.ShockSettings.LowSpeedRebound,
                RearHighSpeedRebound = NotesPage.ShockSettings.HighSpeedRebound,
                RearVolSpc = NotesPage.ShockSettings.VolSpc,
                HasProcessedData = IsComplete,
            };

            await databaseService.PutSessionAsync(newSession);
            session = newSession;
            IsDirty = false;
            IsInDatabase = true;
        }
        catch (Exception e)
        {
            ErrorMessages.Add($"Session could not be saved: {e.Message}");
        }
    }

    protected override Task ResetImplementation()
    {
        Id = session.Id;
        Name = session.Name;

        NotesPage.Description = session.Description;
        NotesPage.ForkSettings.SpringRate = session.FrontSpringRate;
        NotesPage.ForkSettings.VolSpc = session.FrontVolSpc ?? 0;
        NotesPage.ForkSettings.HighSpeedCompression = session.FrontHighSpeedCompression;
        NotesPage.ForkSettings.LowSpeedCompression = session.FrontLowSpeedCompression;
        NotesPage.ForkSettings.LowSpeedRebound = session.FrontLowSpeedRebound;
        NotesPage.ForkSettings.HighSpeedRebound = session.FrontHighSpeedRebound;

        NotesPage.ShockSettings.SpringRate = session.RearSpringRate;
        NotesPage.ShockSettings.VolSpc = session.RearVolSpc ?? 0;
        NotesPage.ShockSettings.HighSpeedCompression = session.RearHighSpeedCompression;
        NotesPage.ShockSettings.LowSpeedCompression = session.RearLowSpeedCompression;
        NotesPage.ShockSettings.LowSpeedRebound = session.RearLowSpeedRebound;
        NotesPage.ShockSettings.HighSpeedRebound = session.RearHighSpeedRebound;

        Timestamp = DateTimeOffset.FromUnixTimeSeconds(session.Timestamp ?? 0).DateTime;

        return Task.CompletedTask;
    }

    #endregion

    #region Commands

    [RelayCommand]
    private async Task Loaded(Rect bounds)
    {
        try
        {
            if (!IsComplete)
            {
                var httpApiService = App.Current?.Services?.GetService<IHttpApiService>();
                var databaseService = App.Current?.Services?.GetService<IDatabaseService>();
                Debug.Assert(httpApiService != null, nameof(httpApiService) + " != null");
                Debug.Assert(databaseService != null, nameof(databaseService) + " != null");

                var psst = await httpApiService.GetSessionPsstAsync(Id) ?? throw new Exception("Session data could not be downloaded from server.");
                await databaseService.PatchSessionPsstAsync(Id, psst);
                session.HasProcessedData = true;
            }

            var cacheLoaded = await LoadCache();
            if (!cacheLoaded ||
                ((SpringPage.FrontTravelHistogram is not null || SpringPage.RearTravelHistogram is not null) && MiscPage.VelocityDistributionComparison is null) ||
                (SpringPage.TravelComparisonHistogram is not null && SpringPage.FrontRearTravelScatter is null) ||
                MiscPage.PositionVelocityComparison is null)
            {
                await CreateCache(bounds);
            }

            if (SpringPage.TravelComparisonHistogram is null)
            {
                var databaseService = App.Current?.Services?.GetService<IDatabaseService>();
                Debug.Assert(databaseService != null, nameof(databaseService) + " != null");

                var telemetryData = await databaseService.GetSessionPsstAsync(Id);
                if (telemetryData is not null && telemetryData.Front.Present && telemetryData.Rear.Present)
                {
                    var b = (Rect)bounds;
                    var (width, height) = ((int)b.Width, (int)(b.Height / 2.0));

                    var tcmp = new TravelHistogramComparisonPlot(new Plot());
                    tcmp.LoadTelemetryData(telemetryData);
                    SpringPage.TravelComparisonHistogram = tcmp.Plot.GetSvgXml(width, height);

                    var cache = await databaseService.GetSessionCacheAsync(Id);
                    if (cache is not null)
                    {
                        cache.TravelComparisonHistogram = SpringPage.TravelComparisonHistogram;
                        await databaseService.PutSessionCacheAsync(cache);
                    }
                }
                else
                {
                    SpringPage.TravelComparisonHistogram = null;
                }
            }

            if (SummaryPage.RunDataRows.Count == 0)
            {
                var summaryDatabaseService = App.Current?.Services?.GetService<IDatabaseService>();
                Debug.Assert(summaryDatabaseService != null, nameof(summaryDatabaseService) + " != null");
                var summaryTelemetry = await summaryDatabaseService.GetSessionPsstAsync(Id);
                if (summaryTelemetry is not null)
                {
                    var data = BuildSummaryData(summaryTelemetry);
                    ApplySummaryData(data);

                    var cache = await summaryDatabaseService.GetSessionCacheAsync(Id);
                    if (cache is not null)
                    {
                        cache.SummaryJson = JsonSerializer.Serialize(data);
                        await summaryDatabaseService.PutSessionCacheAsync(cache);
                    }
                }
            }
        }
        catch (Exception e)
        {
            ErrorMessages.Add($"Could not load session data: {e.Message}");
        }
    }

    #endregion
}
