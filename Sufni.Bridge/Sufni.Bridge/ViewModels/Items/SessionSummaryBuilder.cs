using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using MathNet.Numerics.Statistics;
using Sufni.Bridge.Extensions;
using Sufni.Bridge.Models.Telemetry;
using Sufni.Bridge.ViewModels.SessionPages;

namespace Sufni.Bridge.ViewModels.Items;

internal static class SessionSummaryBuilder
{
    private sealed record SuspensionSummaryStats(
        double MaxTravel,
        double AvgTravel,
        double P95Travel,
        int Bottomouts,
        double AvgCompression,
        double MaxCompression,
        double Comp95th,
        double AvgRebound,
        double MaxRebound,
        double Reb95th);

    internal sealed record CachedSummaryData(
        string[][] RunDataRows,
        string[][] ForkShockRows,
        string[][] WheelRows,
        // Em dash, matching the placeholder BalancePageViewModel uses for an unknown metric —
        // the two sit next to each other in the Summary tab's run-data grid.
        string Airtime = "—",
        string? DataQuality = null);

    private static string FormatCumulativeTravel(TelemetryData telemetryData, SuspensionType type)
    {
        var cum = telemetryData.CalculateCumulativeTravel(type);
        if (cum.Length == 0)
        {
            return "-";
        }

        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{cum[^1] / 1000.0:F1}");
    }

    private static string FormatAirtime(Airtime[]? airtimes)
    {
        if (airtimes is null)
        {
            return "—";
        }

        var total = airtimes.Sum(a => a.End - a.Start);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{total:0.0} s ({airtimes.Length}×)");
    }

    private static string? FormatDataQuality(TelemetryData telemetryData)
    {
        var lines = new List<string>();
        if (telemetryData.FrontDropouts != 0 || telemetryData.RearDropouts != 0)
            lines.Add($"Dropouts: {telemetryData.FrontDropouts} front / {telemetryData.RearDropouts} rear");
        if (telemetryData.Front.ClampedSamples != 0 || telemetryData.Rear.ClampedSamples != 0)
            lines.Add($"Top-out clamps: {telemetryData.Front.ClampedSamples} front / {telemetryData.Rear.ClampedSamples} rear");
        if (telemetryData.Linkage.WheelTravelOffset != 0)
            lines.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Leverage wheel offset: {telemetryData.Linkage.WheelTravelOffset:0.##} mm"));
        if (telemetryData.Linkage.SkippedLeverageRows != 0)
            lines.Add($"Leverage rows skipped: {telemetryData.Linkage.SkippedLeverageRows}");

        return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
    }

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
        var detailedTravel = telemetryData.CalculateDetailedTravelStatistics(type);
        var velocityStats = telemetryData.CalculateVelocityStatistics(type);

        var compressionVels = suspension.Strokes.Compressions
            .SelectMany(s => suspension.Velocity[s.Start..(s.End + 1)])
            .ToList();
        var reboundVels = suspension.Strokes.Rebounds
            .SelectMany(s => suspension.Velocity[s.Start..(s.End + 1)].Select(Math.Abs))
            .ToList();

        return new SuspensionSummaryStats(
            travelStats.Max,
            travelStats.Average,
            detailedTravel.P95,
            travelStats.Bottomouts,
            velocityStats.AverageCompression,
            velocityStats.MaxCompression,
            compressionVels.Count > 0 ? compressionVels.Percentile(95) : 0.0,
            velocityStats.AverageRebound,
            velocityStats.MaxRebound,
            reboundVels.Count > 0 ? -reboundVels.Percentile(95) : 0.0);
    }

    private static SuspensionSummaryStats? BuildForkStats(TelemetryData telemetryData)
    {
        if (!telemetryData.Front.Present || !telemetryData.Linkage.MaxFrontStroke.HasValue || telemetryData.Linkage.MaxFrontStroke <= 0)
        {
            return null;
        }

        var maxForkStroke = telemetryData.Linkage.MaxFrontStroke.Value;
        var frontCoeff = Math.Sin(telemetryData.Linkage.HeadAngle * Math.PI / 180.0);
        if (frontCoeff < 1e-6) return null;
        var invCoeff = 1.0 / frontCoeff;

        var wheelTravel = telemetryData.Front.Travel;
        var wheelVelocity = telemetryData.Front.Velocity;
        var forkTravel = new double[wheelTravel.Length];
        var forkVelocity = new double[wheelVelocity.Length];

        for (var i = 0; i < wheelTravel.Length; i++)
        {
            forkTravel[i] = Math.Min(wheelTravel[i] * invCoeff, maxForkStroke);
        }

        for (var i = 0; i < wheelVelocity.Length; i++)
        {
            forkVelocity[i] = wheelVelocity[i] * invCoeff;
        }

        var compSamples = telemetryData.Front.Strokes.Compressions.Sum(s => s.Stat.Count);
        var rebSamples = telemetryData.Front.Strokes.Rebounds.Sum(s => s.Stat.Count);
        var totalSamples = compSamples + rebSamples + telemetryData.Front.Strokes.Idlings.Sum(s => s.Stat.Count);

        double travelSum = 0.0;
        var travelCount = 0;
        double travelMax = 0.0;
        var travelValues = new List<double>(totalSamples);
        double compressionSum = 0.0;
        var compressionCount = 0;
        double compressionMax = 0.0;
        var compressionVels = new List<double>(compSamples);
        double reboundSum = 0.0;
        var reboundCount = 0;
        double reboundMax = 0.0;
        var reboundVels = new List<double>(rebSamples);

        foreach (var stroke in telemetryData.Front.Strokes.Compressions.Concat(telemetryData.Front.Strokes.Rebounds).Concat(telemetryData.Front.Strokes.Idlings))
        {
            for (var i = stroke.Start; i <= stroke.End && i < forkTravel.Length; i++)
            {
                var t = forkTravel[i];
                travelValues.Add(t);
                travelSum += t;
                travelCount++;
                if (t > travelMax) travelMax = t;
            }
        }

        foreach (var stroke in telemetryData.Front.Strokes.Compressions)
        {
            for (var i = stroke.Start; i <= stroke.End && i < forkVelocity.Length; i++)
            {
                var v = forkVelocity[i];
                compressionSum += v;
                compressionCount++;
                compressionVels.Add(v);
                if (v > compressionMax) compressionMax = v;
            }
        }

        foreach (var stroke in telemetryData.Front.Strokes.Rebounds)
        {
            for (var i = stroke.Start; i <= stroke.End && i < forkVelocity.Length; i++)
            {
                var v = forkVelocity[i];
                reboundSum += v;
                reboundCount++;
                reboundVels.Add(Math.Abs(v));
                if (v < reboundMax) reboundMax = v;
            }
        }

        var bottomouts = 0;
        var threshold = maxForkStroke * 0.97;
        for (var i = 0; i < forkTravel.Length; i++)
        {
            if (forkTravel[i] <= threshold) continue;
            bottomouts++;
            while (i < forkTravel.Length && forkTravel[i] > threshold) i++;
        }

        if (travelCount == 0) return null;

        return new SuspensionSummaryStats(
            travelMax,
            travelSum / travelCount,
            travelValues.Count > 0 ? travelValues.Percentile(95) : 0.0,
            bottomouts,
            compressionCount > 0 ? compressionSum / compressionCount : 0.0,
            compressionMax,
            compressionVels.Count > 0 ? compressionVels.Percentile(95) : 0.0,
            reboundCount > 0 ? reboundSum / reboundCount : 0.0,
            reboundMax,
            reboundVels.Count > 0 ? -reboundVels.Percentile(95) : 0.0);
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

        Parallel.For(0, rearTravel.Length, i =>
        {
            var s = SolveShockTravel(rearTravel[i], coeffs, maxShockStroke);
            shockTravel[i] = s;
            var derivative = EvaluateDerivative(coeffs, s);
            shockVelocity[i] = Math.Abs(derivative) > 1e-6 ? rearVelocity[i] / derivative : 0.0;
        });

        var compSamples = telemetryData.Rear.Strokes.Compressions.Sum(s => s.Stat.Count);
        var rebSamples = telemetryData.Rear.Strokes.Rebounds.Sum(s => s.Stat.Count);
        var totalSamples = compSamples + rebSamples + telemetryData.Rear.Strokes.Idlings.Sum(s => s.Stat.Count);

        double travelSum = 0.0;
        var travelCount = 0;
        double travelMax = 0.0;
        var travelValues = new List<double>(totalSamples);
        double compressionSum = 0.0;
        var compressionCount = 0;
        double compressionMax = 0.0;
        var compressionVels = new List<double>(compSamples);
        double reboundSum = 0.0;
        var reboundCount = 0;
        double reboundMax = 0.0;
        var reboundVels = new List<double>(rebSamples);

        foreach (var stroke in telemetryData.Rear.Strokes.Compressions.Concat(telemetryData.Rear.Strokes.Rebounds).Concat(telemetryData.Rear.Strokes.Idlings))
        {
            for (var i = stroke.Start; i <= stroke.End && i < shockTravel.Length; i++)
            {
                var t = shockTravel[i];
                travelValues.Add(t);
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
                compressionVels.Add(v);
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
                reboundVels.Add(Math.Abs(v));
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
            travelValues.Count > 0 ? travelValues.Percentile(95) : 0.0,
            bottomouts,
            compressionCount > 0 ? compressionSum / compressionCount : 0.0,
            compressionMax,
            compressionVels.Count > 0 ? compressionVels.Percentile(95) : 0.0,
            reboundCount > 0 ? reboundSum / reboundCount : 0.0,
            reboundMax,
            reboundVels.Count > 0 ? -reboundVels.Percentile(95) : 0.0);
    }

    internal static Task<CachedSummaryData> PopulateSummary(SessionViewModel viewModel, TelemetryData telemetryData) =>
        PopulateSummary(viewModel, telemetryData,
            Task.FromResult(telemetryData.Front.Present
                ? (VelocityBands?)telemetryData.CalculateVelocityBands(
                    SuspensionType.Front,
                    200,
                    telemetryData.FrontVelocityDeadBand())
                : null),
            Task.FromResult(telemetryData.Rear.Present
                ? (VelocityBands?)telemetryData.CalculateVelocityBands(
                    SuspensionType.Rear,
                    200,
                    telemetryData.RearWheelVelocityDeadBand())
                : null));

    internal static async Task<CachedSummaryData> PopulateSummary(
        SessionViewModel viewModel,
        TelemetryData telemetryData,
        Task<VelocityBands?> frontBandsTask,
        Task<VelocityBands?> rearBandsTask)
    {
        var date = (viewModel.Timestamp ?? DateTime.UnixEpoch).ToString("dd-MM-yyyy");
        var time = (viewModel.Timestamp ?? DateTime.UnixEpoch).ToString("HH:mm");
        var sampleCount = Math.Max(telemetryData.Front.Travel?.Length ?? 0, telemetryData.Rear.Travel?.Length ?? 0);
        var duration = telemetryData.SampleRate > 0
            ? TimeSpan.FromSeconds(sampleCount / (double)telemetryData.SampleRate)
            : TimeSpan.Zero;
        var runDuration = duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss")
            : duration.ToString(@"m\:ss");

        // Run the independent (read-only) computations in parallel
        var forkStatsTask = Task.Run(() => BuildForkStats(telemetryData));
        var frontWheelStatsTask = Task.Run(() => BuildWheelStats(telemetryData, SuspensionType.Front));
        var shockStatsTask = Task.Run(() => BuildShockStats(telemetryData));
        var rearWheelStatsTask = Task.Run(() => BuildWheelStats(telemetryData, SuspensionType.Rear));
        await Task.WhenAll(forkStatsTask, frontWheelStatsTask, shockStatsTask, rearWheelStatsTask, frontBandsTask, rearBandsTask);

        var forkStats = forkStatsTask.Result;
        var shockStats = shockStatsTask.Result;
        var frontWheelStats = frontWheelStatsTask.Result;
        var rearWheelStats = rearWheelStatsTask.Result;
        var frontBands = frontBandsTask.Result;
        var rearBands = rearBandsTask.Result;

        var runDataRows = new ObservableCollection<SummaryValueRow>(
        [
            new SummaryValueRow("Date", date),
            new SummaryValueRow("Time", time),
            new SummaryValueRow("Run duration", runDuration),
        ]);

        var forkShockRows = new ObservableCollection<SummaryComparisonRow>(
        [
            new SummaryComparisonRow("Pos [AVG]",
                forkStats is null ? "-" : SessionFormat.TravelWithUnits(forkStats.AvgTravel, telemetryData.Linkage.MaxFrontStroke ?? 0),
                shockStats is null ? "-" : SessionFormat.TravelWithUnits(shockStats.AvgTravel, telemetryData.Linkage.MaxRearStroke ?? 0)),
            new SummaryComparisonRow("Pos [95th]",
                forkStats is null ? "-" : SessionFormat.TravelWithUnits(forkStats.P95Travel, telemetryData.Linkage.MaxFrontStroke ?? 0),
                shockStats is null ? "-" : SessionFormat.TravelWithUnits(shockStats.P95Travel, telemetryData.Linkage.MaxRearStroke ?? 0)),
            new SummaryComparisonRow("Pos [MAX]",
                forkStats is null ? "-" : SessionFormat.TravelWithUnits(forkStats.MaxTravel, telemetryData.Linkage.MaxFrontStroke ?? 0),
                shockStats is null ? "-" : SessionFormat.TravelWithUnits(shockStats.MaxTravel, telemetryData.Linkage.MaxRearStroke ?? 0)),
            new SummaryComparisonRow("Bottom out",
                forkStats is null ? "-" : SessionFormat.BottomoutsWithUnits(forkStats.Bottomouts),
                shockStats is null ? "-" : SessionFormat.BottomoutsWithUnits(shockStats.Bottomouts)),
            new SummaryComparisonRow("Comp [AVG]",
                forkStats is null ? "-" : SessionFormat.VelocityWithUnits(forkStats.AvgCompression),
                shockStats is null ? "-" : SessionFormat.VelocityWithUnits(shockStats.AvgCompression)),
            new SummaryComparisonRow("Reb [AVG]",
                forkStats is null ? "-" : SessionFormat.VelocityWithUnits(forkStats.AvgRebound),
                shockStats is null ? "-" : SessionFormat.VelocityWithUnits(shockStats.AvgRebound)),
            new SummaryComparisonRow("Comp [95th]",
                forkStats is null ? "-" : SessionFormat.VelocityWithUnits(forkStats.Comp95th),
                shockStats is null ? "-" : SessionFormat.VelocityWithUnits(shockStats.Comp95th)),
            new SummaryComparisonRow("Reb [95th]",
                forkStats is null ? "-" : SessionFormat.VelocityWithUnits(forkStats.Reb95th),
                shockStats is null ? "-" : SessionFormat.VelocityWithUnits(shockStats.Reb95th)),
            new SummaryComparisonRow("Comp [MAX]",
                forkStats is null ? "-" : SessionFormat.VelocityWithUnits(forkStats.MaxCompression),
                shockStats is null ? "-" : SessionFormat.VelocityWithUnits(shockStats.MaxCompression)),
            new SummaryComparisonRow("Reb [MAX]",
                forkStats is null ? "-" : SessionFormat.VelocityWithUnits(forkStats.MaxRebound),
                shockStats is null ? "-" : SessionFormat.VelocityWithUnits(shockStats.MaxRebound))
        ]);

        var wheelRows = new ObservableCollection<SummaryComparisonRow>(
        [
            new SummaryComparisonRow("Pos [AVG]",
                frontWheelStats is null ? "-" : SessionFormat.TravelWithUnits(frontWheelStats.AvgTravel, telemetryData.Linkage.MaxFrontTravel),
                rearWheelStats is null ? "-" : SessionFormat.TravelWithUnits(rearWheelStats.AvgTravel, telemetryData.Linkage.MaxRearTravel)),
            new SummaryComparisonRow("Pos [95th]",
                frontWheelStats is null ? "-" : SessionFormat.TravelWithUnits(frontWheelStats.P95Travel, telemetryData.Linkage.MaxFrontTravel),
                rearWheelStats is null ? "-" : SessionFormat.TravelWithUnits(rearWheelStats.P95Travel, telemetryData.Linkage.MaxRearTravel)),
            new SummaryComparisonRow("Pos [MAX]",
                frontWheelStats is null ? "-" : SessionFormat.TravelWithUnits(frontWheelStats.MaxTravel, telemetryData.Linkage.MaxFrontTravel),
                rearWheelStats is null ? "-" : SessionFormat.TravelWithUnits(rearWheelStats.MaxTravel, telemetryData.Linkage.MaxRearTravel)),
            new SummaryComparisonRow("Bottom out",
                frontWheelStats is null ? "-" : SessionFormat.BottomoutsWithUnits(frontWheelStats.Bottomouts),
                rearWheelStats is null ? "-" : SessionFormat.BottomoutsWithUnits(rearWheelStats.Bottomouts)),
            new SummaryComparisonRow("Comp [AVG]",
                frontWheelStats is null ? "-" : SessionFormat.VelocityWithUnits(frontWheelStats.AvgCompression),
                rearWheelStats is null ? "-" : SessionFormat.VelocityWithUnits(rearWheelStats.AvgCompression)),
            new SummaryComparisonRow("Reb [AVG]",
                frontWheelStats is null ? "-" : SessionFormat.VelocityWithUnits(frontWheelStats.AvgRebound),
                rearWheelStats is null ? "-" : SessionFormat.VelocityWithUnits(rearWheelStats.AvgRebound)),
            new SummaryComparisonRow("Comp [95th]",
                frontWheelStats is null ? "-" : SessionFormat.VelocityWithUnits(frontWheelStats.Comp95th),
                rearWheelStats is null ? "-" : SessionFormat.VelocityWithUnits(rearWheelStats.Comp95th)),
            new SummaryComparisonRow("Reb [95th]",
                frontWheelStats is null ? "-" : SessionFormat.VelocityWithUnits(frontWheelStats.Reb95th),
                rearWheelStats is null ? "-" : SessionFormat.VelocityWithUnits(rearWheelStats.Reb95th)),
            new SummaryComparisonRow("Comp [MAX]",
                frontWheelStats is null ? "-" : SessionFormat.VelocityWithUnits(frontWheelStats.MaxCompression),
                rearWheelStats is null ? "-" : SessionFormat.VelocityWithUnits(rearWheelStats.MaxCompression)),
            new SummaryComparisonRow("Reb [MAX]",
                frontWheelStats is null ? "-" : SessionFormat.VelocityWithUnits(frontWheelStats.MaxRebound),
                rearWheelStats is null ? "-" : SessionFormat.VelocityWithUnits(rearWheelStats.MaxRebound)),
            new SummaryComparisonRow("HSR [%]",
                frontBands is null ? "-" : SessionFormat.Percent(frontBands.HighSpeedRebound),
                rearBands is null ? "-" : SessionFormat.Percent(rearBands.HighSpeedRebound)),
            new SummaryComparisonRow("LSR [%]",
                frontBands is null ? "-" : SessionFormat.Percent(frontBands.LowSpeedRebound),
                rearBands is null ? "-" : SessionFormat.Percent(rearBands.LowSpeedRebound)),
            new SummaryComparisonRow("LSC [%]",
                frontBands is null ? "-" : SessionFormat.Percent(frontBands.LowSpeedCompression),
                rearBands is null ? "-" : SessionFormat.Percent(rearBands.LowSpeedCompression)),
            new SummaryComparisonRow("HSC [%]",
                frontBands is null ? "-" : SessionFormat.Percent(frontBands.HighSpeedCompression),
                rearBands is null ? "-" : SessionFormat.Percent(rearBands.HighSpeedCompression)),
            new SummaryComparisonRow("Cum. Travel [m]",
                telemetryData.Front.Present ? FormatCumulativeTravel(telemetryData, SuspensionType.Front) : "-",
                telemetryData.Rear.Present ? FormatCumulativeTravel(telemetryData, SuspensionType.Rear) : "-")
        ]);

        var airtime = FormatAirtime(telemetryData.Airtimes);
        var dataQuality = FormatDataQuality(telemetryData);

        Dispatcher.UIThread.Post(() =>
        {
            viewModel.SummaryPage.RunDataRows = runDataRows;
            viewModel.SummaryPage.ForkShockRows = forkShockRows;
            viewModel.SummaryPage.WheelRows = wheelRows;
            viewModel.SummaryPage.Airtime = airtime;
            viewModel.SummaryPage.DataQuality = dataQuality;
        });

        return new CachedSummaryData(
            runDataRows.Select(r => new[] { r.Label, r.Value }).ToArray(),
            forkShockRows.Select(r => new[] { r.Label, r.LeftValue, r.RightValue }).ToArray(),
            wheelRows.Select(r => new[] { r.Label, r.LeftValue, r.RightValue }).ToArray(),
            airtime,
            dataQuality);
    }
}
