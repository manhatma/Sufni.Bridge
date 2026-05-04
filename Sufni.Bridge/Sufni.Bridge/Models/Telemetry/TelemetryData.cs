using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using MathNet.Numerics;
using MathNet.Numerics.Distributions;
using MathNet.Numerics.IntegralTransforms;
using MathNet.Numerics.Statistics;
using MessagePack;
using Generate = ScottPlot.Generate;
using Sufni.Bridge.Models;

#pragma warning disable CS8618

namespace Sufni.Bridge.Models.Telemetry;

[MessagePackObject(keyAsPropertyName: true)]
public class Airtime
{
    public double Start { get; set; }
    public double End { get; set; }
};

[MessagePackObject(keyAsPropertyName: true)]
public class Suspension
{
    public bool Present { get; set; }
    public Calibration? Calibration { get; set; }
    public double[] Travel { get; set; }
    public double[] Velocity { get; set; }
    public Strokes Strokes { get; set; }
    public double[] TravelBins { get; set; }
    public double[] VelocityBins { get; set; }
    public double[] FineVelocityBins { get; set; }
};

public record HistogramData(List<double> Bins, List<double> Values);
public record StackedHistogramData(List<double> Bins, List<double[]> Values);

public record TravelStatistics(double Max, double Average, int Bottomouts);
public record DetailedTravelStatistics(double Max, double Average, double P95, int Bottomouts);
public record DetailedTravelHistogramData(
    List<double> TravelMidsMm,
    List<double> TravelMidsPercentage,
    List<double> TimePercentage,
    List<double> BarWidthsMm,
    List<double> BarWidthsPercentage,
    double MaxTravelMm);

public record VelocityStatistics(
    double AverageRebound,
    double MaxRebound,
    double AverageCompression,
    double MaxCompression);

public record NormalDistributionData(
    List<double> Y,
    List<double> Pdf);

public record VelocityBands(
    double LowSpeedCompression,
    double HighSpeedCompression,
    double LowSpeedRebound,
    double HighSpeedRebound);

public enum SuspensionType
{
    Front,
    Rear
}

public enum BalanceType
{
    Compression,
    Rebound
}

public record PositionVelocityData(double[] Travel, double[] Velocity);

public record BalanceData(
    List<double> FrontTravel,
    List<double> FrontVelocity,
    List<double> FrontTrend,
    List<double> RearTravel,
    List<double> RearVelocity,
    List<double> RearTrend,
    double MeanSignedDeviation);

public record TravelSpectrum(double[] Frequencies, double[] Amplitudes);

public record BalanceMetrics(
    double? FrontSagPct,
    double? RearSagPct,
    double? SagDifferencePp,
    double? FrontP95Pct,
    double? RearP95Pct,
    int? FrontBottomouts,
    int? RearBottomouts,
    double? CompressionVelocityRatio,
    double? ReboundVelocityRatio,
    double? CompressionMsd,
    double? ReboundMsd,
    double? FrontPeakFrequencyHz,
    double? RearPeakFrequencyHz,
    double? FrequencyDifferenceHz,
    double? PeakAmplitudeRatio,
    double? LowEnergyRatioDb,
    double? MidEnergyRatioDb,
    double? WheelEnergyRatioDb,
    double? HighEnergyRatioDb,
    double? LowCoherence,
    double? MidCoherence,
    double? WheelCoherence,
    double? HighCoherence,
    double? FrequencySplitHz,
    // Wheel-load metrics (V1, dyno-spring-based force estimator).
    // All null when no force estimator is configured for the session/setup.
    double? FrontStaticForceN = null,
    double? RearStaticForceN = null,
    // Flat-floor reference: wheel load this setup would produce at the centre of the
    // sag-target band. Used to derive a bike-specific expected front/rear share.
    double? FrontStaticForceFlatN = null,
    double? RearStaticForceFlatN = null,
    double? FrontUnloadingPct = null,
    double? RearUnloadingPct = null,
    double? UnloadingDifferencePp = null,
    int? FrontUnloadingEvents = null,
    int? RearUnloadingEvents = null,
    double? LowLoadMichelson = null,
    double? MidLoadMichelson = null,
    double? WheelLoadMichelson = null,
    double? HighLoadMichelson = null,
    // Dynamic range expressed as a factor (max−min)/static. 1.0 = oscillates within
    // ±static; 3.0 = swings 3× the static load (typical for medium trails).
    double? FrontDynamicRangeFactor = null,
    double? RearDynamicRangeFactor = null,
    double? DynamicRangeDifference = null,
    // Schema marker for cached metrics. Bump when computation logic changes in a way that
    // invalidates older cached results (e.g. airtime-aware unloading, threshold tuning).
    // Caches with a lower version trigger a background recompute on session open.
    int WheelLoadSchemaVersion = 0);

[MessagePackObject(keyAsPropertyName: true)]
public class TelemetryData
{
    public const int TravelBinsForVelocityHistogram = 10;

    // Increment when velocity processing parameters change (e.g. smoother lambda).
    // Blobs with a lower version are automatically re-processed from Travel arrays on load.
    public const int CurrentProcessingVersion = 4;

    // Increment when wheel-load metrics computation changes in a way that invalidates
    // cached BalanceMetricsJson.
    //   v1: airtime-aware unloading + share-based static load
    //   v2: bike-specific flat-reference static load (StaticForceFlatN per axle)
    //   v3: first damper-curve assets shipped — recompute so the damper contribution
    //       reaches sessions whose setup gains a damper assignment after this update
    //   v4: motion-based airtime detection (replaces strict top-out heuristic) so jumps
    //       on real air-shocks no longer pollute Front/Rear unloading metrics
    //   v5: tightened unloading thresholds (20 % / 100 ms), dynamic range expressed as a
    //       factor (max−min)/static instead of a percentage
    //   v6: Schmitt-trigger hysteresis on unloading counter (enter 20 %, exit 30 %);
    //       airtime detector min-duration 200 ms → 100 ms to catch small hops
    public const int CurrentBalanceMetricsSchemaVersion = 6;

    #region Public properties

    public string Name { get; set; }
    public int Version { get; set; }
    public int ProcessingVersion { get; set; }
    public int SampleRate { get; set; }
    public int Timestamp { get; set; }
    public Suspension Front { get; set; }
    public Suspension Rear { get; set; }
    public Linkage Linkage { get; set; }
    public Airtime[] Airtimes { get; set; }

    #endregion

    #region Constructors

    public TelemetryData() { }

    public TelemetryData(string name, int version, int sampleRate, int timestamp,
        Calibration? frontCal, Calibration? rearCal, Linkage linkage)
    {
        Name = name;
        Version = version;
        SampleRate = sampleRate;
        Timestamp = timestamp;
        Linkage = linkage;

        Front = new Suspension
        {
            Calibration = frontCal,
            Strokes = new Strokes()
        };

        Rear = new Suspension
        {
            Calibration = rearCal,
            Strokes = new Strokes()
        };
    }

    #endregion

    #region Private helpers for ProcessRecording

    private static double[] Linspace(double min, double max, int num)
    {
        var step = (max - min) / (num - 1);
        var bins = new double[num];

        for (var i = 0; i < num; i++)
        {
            bins[i] = min + step * i;
        }

        return bins;
    }

    private static int[] Digitize(double[] data, double[] bins)
    {
        var inds = new int[data.Length];
        for (var k = 0; k < data.Length; k++)
        {
            var i = Array.BinarySearch(bins, data[k]);
            if (i < 0) i = ~i;
            // If current value is not exactly a bin boundary, we subtract 1 to make
            // the digitized slice indexed from 0 instead of 1. We do the same if a
            // value would exceed existing bins.
            if (data[k] >= bins[^1] || Math.Abs(data[k] - bins[i]) > 0.0001)
            {
                i -= 1;
            }
            inds[k] = i;
        }
        return inds;
    }

    private void CalculateAirTimes()
    {
        var airtimes = new List<Airtime>();

        if (Front.Present && Rear.Present)
        {
            foreach (var f in Front.Strokes.Idlings)
            {
                if (!f.AirCandidate) continue;
                foreach (var r in Rear.Strokes.Idlings)
                {
                    if (!r.AirCandidate || !f.Overlaps(r)) continue;
                    f.AirCandidate = false;
                    r.AirCandidate = false;

                    var at = new Airtime
                    {
                        Start = Math.Min(f.Start, r.Start) / (double)SampleRate,
                        End = Math.Min(f.End, r.End) / (double)SampleRate
                    };
                    airtimes.Add(at);
                    break;
                }
            }

            var maxMean = (Linkage.MaxFrontTravel + Linkage.MaxRearTravel) / 2.0;

            foreach (var f in Front.Strokes.Idlings)
            {
                if (!f.AirCandidate) continue;
                var fMean = Front.Travel[f.Start..(f.End + 1)].Mean();
                var rMean = Rear.Travel[f.Start..(f.End + 1)].Mean();

                if (!((fMean + rMean) / 2 <= maxMean * Parameters.AirtimeTravelMeanThresholdRatio)) continue;
                var at = new Airtime
                {
                    Start = f.Start / (double)SampleRate,
                    End = f.End / (double)SampleRate
                };
                airtimes.Add(at);
            }

            foreach (var r in Rear.Strokes.Idlings)
            {
                if (!r.AirCandidate) continue;
                var fMean = Front.Travel[r.Start..(r.End + 1)].Mean();
                var rMean = Rear.Travel[r.Start..(r.End + 1)].Mean();

                if (!((fMean + rMean) / 2 <= maxMean * Parameters.AirtimeTravelMeanThresholdRatio)) continue;
                var at = new Airtime
                {
                    Start = r.Start / (double)SampleRate,
                    End = r.End / (double)SampleRate
                };
                airtimes.Add(at);
            }
        }
        else if (Front.Present)
        {
            foreach (var f in Front.Strokes.Idlings)
            {
                if (!f.AirCandidate) continue;
                var at = new Airtime
                {
                    Start = f.Start / (double)SampleRate,
                    End = f.End / (double)SampleRate
                };
                airtimes.Add(at);
            }
        }
        else if (Rear.Present)
        {
            foreach (var r in Rear.Strokes.Idlings)
            {
                if (!r.AirCandidate) continue;
                var at = new Airtime
                {
                    Start = r.Start / (double)SampleRate,
                    End = r.End / (double)SampleRate
                };
                airtimes.Add(at);
            }
        }

        Airtimes = [.. airtimes];
    }

    private static double[] ComputeVelocity(double[] travel, int sampleRate)
    {
        var n = travel.Length;
        var v = new double[n];

        v[0] = (travel[1] - travel[0]) * sampleRate;
        for (var i = 1; i < n - 1; i++)
        {
            v[i] = (travel[i + 1] - travel[i - 1]) * sampleRate / 2.0;
        }
        v[n - 1] = (travel[n - 1] - travel[n - 2]) * sampleRate;

        return v;
    }

    private static (double[], int[]) DigitizeVelocity(double[] v, double step)
    {
        // 0 lies on a bin edge — negative and positive velocities get separate bins
        var mn = Math.Floor(v.Min() / step) * step;
        var mx = (Math.Floor(v.Max() / step) + 1) * step;
        var bins = Linspace(mn, mx, (int)((mx - mn) / step) + 1);
        var data = Digitize(v, bins);
        return (bins, data);
    }

    #endregion

    #region PSST conversion

    public byte[] ProcessRecording(ushort[] front, ushort[] rear)
    {
        // Evaluate front and rear input arrays
        var fc = front.Length;
        var rc = rear.Length;
        Front.Present = fc != 0;
        Rear.Present = rc != 0;
        if (!Front.Present && !Rear.Present)
        {
            throw new Exception("Front and rear record arrays are empty!");
        }
        if (Front.Present && Rear.Present && fc != rc)
        {
            throw new Exception("Front and rear record counts are not equal!");
        }

        // Create Whittaker-Henderson smoother for velocity smoothing
        var smoother = new WhittakerHendersonSmoother(2, 5);

        if (Front.Present)
        {
            Front.Travel = new double[fc];
            var frontCoeff = Math.Sin(Linkage.HeadAngle * Math.PI / 180.0);

            for (var i = 0; i < front.Length; i++)
            {
                // Front travel might under/overshoot because of erroneous data
                // acquisition. Errors might occur mid-ride (e.g. broken electrical
                // connection due to vibration), so we don't error out, just cap
                // travel. Errors like these will be obvious on the graphs, and
                // the affected regions can be filtered by hand.
                var travel = Front.Calibration!.Evaluate(front[i]);
                var x = travel * frontCoeff;
                x = Math.Max(0, x);
                x = Math.Min(x, Linkage.MaxFrontTravel);
                Front.Travel[i] = x;
            }

            var tbins = Linspace(0, Linkage.MaxFrontTravel, Parameters.TravelHistBins + 1);
            var dt = Digitize(Front.Travel, tbins);
            Front.TravelBins = tbins;

            var v = ComputeVelocity(smoother.Smooth(Front.Travel), SampleRate);
            Front.Velocity = v;
            var (vbins, dv) = DigitizeVelocity(v, Parameters.VelocityHistStep);
            Front.VelocityBins = vbins;
            var (vbinsFine, dvFine) = DigitizeVelocity(v, Parameters.VelocityHistStepFine);
            Front.FineVelocityBins = vbinsFine;

            var strokes = Strokes.FilterStrokes(v, Front.Travel, Linkage.MaxFrontTravel, SampleRate);
            Front.Strokes.Categorize(strokes);
            if (Front.Strokes.Compressions.Length == 0 && Front.Strokes.Rebounds.Length == 0)
            {
                Front.Present = false;
            }
            else
            {
                Front.Strokes.Digitize(dt, dv, dvFine);
            }
        }

        if (Rear.Present)
        {
            Rear.Travel = new double[rc];

            for (var i = 0; i < rear.Length; i++)
            {
                // Rear travel might also overshoot the max because of
                //  a) inaccurately measured leverage ratio
                //  b) inaccuracies introduced by polynomial fitting
                // So we just cap it at calculated maximum.
                var travel = Rear.Calibration!.Evaluate(rear[i]);
                var x = Linkage.Polynomial.Evaluate(travel);
                x = Math.Max(0, x);
                x = Math.Min(x, Linkage.MaxRearTravel);
                Rear.Travel[i] = x;
            }

            var tbins = Linspace(0, Linkage.MaxRearTravel, Parameters.TravelHistBins + 1);
            var dt = Digitize(Rear.Travel, tbins);
            Rear.TravelBins = tbins;

            var v = ComputeVelocity(smoother.Smooth(Rear.Travel), SampleRate);
            Rear.Velocity = v;
            var (vbins, dv) = DigitizeVelocity(v, Parameters.VelocityHistStep);
            Rear.VelocityBins = vbins;
            var (vbinsFine, dvFine) = DigitizeVelocity(v, Parameters.VelocityHistStepFine);
            Rear.FineVelocityBins = vbinsFine;

            var strokes = Strokes.FilterStrokes(v, Rear.Travel, Linkage.MaxRearTravel, SampleRate);
            Rear.Strokes.Categorize(strokes);
            if (Rear.Strokes.Compressions.Length == 0 && Rear.Strokes.Rebounds.Length == 0)
            {
                Rear.Present = false;
            }
            else
            {
                Rear.Strokes.Digitize(dt, dv, dvFine);
            }
        }

        CalculateAirTimes();
        ProcessingVersion = CurrentProcessingVersion;

        return MessagePackSerializer.Serialize(this);
    }

    /// <summary>
    /// Re-derives Velocity, Strokes, VelocityBins from the stored Travel arrays
    /// using current smoother parameters. Called when ProcessingVersion is outdated.
    /// Returns the updated serialized blob.
    /// </summary>
    public byte[] ReprocessVelocity()
    {
        var smoother = new WhittakerHendersonSmoother(2, 5);

        if (Front.Present)
        {
            var tbins = Linspace(0, Linkage.MaxFrontTravel, Parameters.TravelHistBins + 1);
            var dt = Digitize(Front.Travel, tbins);
            Front.TravelBins = tbins;

            var v = ComputeVelocity(smoother.Smooth(Front.Travel), SampleRate);
            Front.Velocity = v;
            var (vbins, dv) = DigitizeVelocity(v, Parameters.VelocityHistStep);
            Front.VelocityBins = vbins;
            var (vbinsFine, dvFine) = DigitizeVelocity(v, Parameters.VelocityHistStepFine);
            Front.FineVelocityBins = vbinsFine;

            Front.Strokes = new Strokes();
            var strokes = Strokes.FilterStrokes(v, Front.Travel, Linkage.MaxFrontTravel, SampleRate);
            Front.Strokes.Categorize(strokes);
            if (Front.Strokes.Compressions.Length == 0 && Front.Strokes.Rebounds.Length == 0)
                Front.Present = false;
            else
                Front.Strokes.Digitize(dt, dv, dvFine);
        }

        if (Rear.Present)
        {
            var tbins = Linspace(0, Linkage.MaxRearTravel, Parameters.TravelHistBins + 1);
            var dt = Digitize(Rear.Travel, tbins);
            Rear.TravelBins = tbins;

            var v = ComputeVelocity(smoother.Smooth(Rear.Travel), SampleRate);
            Rear.Velocity = v;
            var (vbins, dv) = DigitizeVelocity(v, Parameters.VelocityHistStep);
            Rear.VelocityBins = vbins;
            var (vbinsFine, dvFine) = DigitizeVelocity(v, Parameters.VelocityHistStepFine);
            Rear.FineVelocityBins = vbinsFine;

            Rear.Strokes = new Strokes();
            var strokes = Strokes.FilterStrokes(v, Rear.Travel, Linkage.MaxRearTravel, SampleRate);
            Rear.Strokes.Categorize(strokes);
            if (Rear.Strokes.Compressions.Length == 0 && Rear.Strokes.Rebounds.Length == 0)
                Rear.Present = false;
            else
                Rear.Strokes.Digitize(dt, dv, dvFine);
        }

        CalculateAirTimes();
        ProcessingVersion = CurrentProcessingVersion;

        return MessagePackSerializer.Serialize(this);
    }

    #endregion

    /// <summary>
    /// Concatenates travel arrays from multiple sessions with a linear ramp between each pair.
    /// Without the ramp, a step discontinuity in travel (e.g. 50mm → 5mm) produces a massive
    /// velocity spike when differentiated, corrupting stroke statistics.
    /// </summary>
    private static double[] ConcatenateTravelWithTransitions(List<double[]> travelArrays, int sampleRate)
    {
        // Transition duration: 0.5s — long enough that even a full-travel ramp
        // stays within normal velocity range (e.g. 200mm / 0.5s = 400 mm/s)
        var transitionSamples = sampleRate / 2;
        var result = new List<double>(travelArrays.Sum(a => a.Length) + transitionSamples * (travelArrays.Count - 1));

        for (var s = 0; s < travelArrays.Count; s++)
        {
            if (s > 0)
            {
                var from = result[^1];
                var to = travelArrays[s][0];
                for (var i = 1; i <= transitionSamples; i++)
                    result.Add(from + (to - from) * i / transitionSamples);
            }

            result.AddRange(travelArrays[s]);
        }

        return result.ToArray();
    }

    /// <summary>
    /// Creates an in-memory copy of this TelemetryData with travel arrays sliced to [startSample..endSample].
    /// Velocity, Strokes, Bins are fully recomputed on the cropped travel.
    /// The original is not modified and nothing is persisted.
    /// </summary>
    public TelemetryData CreateCroppedCopy(int startSample, int endSample)
    {
        var cropped = new TelemetryData
        {
            Name = Name,
            Version = Version,
            SampleRate = SampleRate,
            Timestamp = Timestamp,
            Linkage = Linkage,
            Front = new Suspension { Present = Front.Present, Calibration = Front.Calibration, Strokes = new Strokes() },
            Rear  = new Suspension { Present = Rear.Present,  Calibration = Rear.Calibration,  Strokes = new Strokes() }
        };

        var smoother = new WhittakerHendersonSmoother(2, 5);

        if (Front.Present && Front.Travel.Length > 0)
        {
            var s = Math.Max(0, Math.Min(startSample, Front.Travel.Length - 1));
            var e = Math.Max(s + 1, Math.Min(endSample, Front.Travel.Length));
            cropped.Front.Travel = Front.Travel[s..e];

            var tbins = Linspace(0, Linkage.MaxFrontTravel, Parameters.TravelHistBins + 1);
            var dt = Digitize(cropped.Front.Travel, tbins);
            cropped.Front.TravelBins = tbins;

            var v = ComputeVelocity(smoother.Smooth(cropped.Front.Travel), SampleRate);
            cropped.Front.Velocity = v;
            var (vbins, dv) = DigitizeVelocity(v, Parameters.VelocityHistStep);
            cropped.Front.VelocityBins = vbins;
            var (vbinsFine, dvFine) = DigitizeVelocity(v, Parameters.VelocityHistStepFine);
            cropped.Front.FineVelocityBins = vbinsFine;

            var strokes = Strokes.FilterStrokes(v, cropped.Front.Travel, Linkage.MaxFrontTravel, SampleRate);
            cropped.Front.Strokes.Categorize(strokes);
            if (cropped.Front.Strokes.Compressions.Length == 0 && cropped.Front.Strokes.Rebounds.Length == 0)
                cropped.Front.Present = false;
            else
                cropped.Front.Strokes.Digitize(dt, dv, dvFine);
        }

        if (Rear.Present && Rear.Travel.Length > 0)
        {
            var s = Math.Max(0, Math.Min(startSample, Rear.Travel.Length - 1));
            var e = Math.Max(s + 1, Math.Min(endSample, Rear.Travel.Length));
            cropped.Rear.Travel = Rear.Travel[s..e];

            var tbins = Linspace(0, Linkage.MaxRearTravel, Parameters.TravelHistBins + 1);
            var dt = Digitize(cropped.Rear.Travel, tbins);
            cropped.Rear.TravelBins = tbins;

            var v = ComputeVelocity(smoother.Smooth(cropped.Rear.Travel), SampleRate);
            cropped.Rear.Velocity = v;
            var (vbins, dv) = DigitizeVelocity(v, Parameters.VelocityHistStep);
            cropped.Rear.VelocityBins = vbins;
            var (vbinsFine, dvFine) = DigitizeVelocity(v, Parameters.VelocityHistStepFine);
            cropped.Rear.FineVelocityBins = vbinsFine;

            var strokes = Strokes.FilterStrokes(v, cropped.Rear.Travel, Linkage.MaxRearTravel, SampleRate);
            cropped.Rear.Strokes.Categorize(strokes);
            if (cropped.Rear.Strokes.Compressions.Length == 0 && cropped.Rear.Strokes.Rebounds.Length == 0)
                cropped.Rear.Present = false;
            else
                cropped.Rear.Strokes.Digitize(dt, dv, dvFine);
        }

        cropped.CalculateAirTimes();
        return cropped;
    }

    public static TelemetryData CombineSessions(List<TelemetryData> sessions, string name)
    {
        if (sessions.Count < 2)
            throw new ArgumentException("At least 2 sessions required to combine.");

        var first = sessions[0];
        foreach (var s in sessions.Skip(1))
        {
            if (s.SampleRate != first.SampleRate)
                throw new InvalidOperationException("All sessions must have the same sample rate.");
            if (Math.Abs(s.Linkage.MaxFrontTravel - first.Linkage.MaxFrontTravel) > 0.01 ||
                Math.Abs(s.Linkage.MaxRearTravel - first.Linkage.MaxRearTravel) > 0.01)
                throw new InvalidOperationException("All sessions must have compatible linkage (same max travel).");
        }

        var hasFront = sessions.All(s => s.Front.Present);
        var hasRear = sessions.All(s => s.Rear.Present);

        var combined = new TelemetryData
        {
            Name = name,
            Version = first.Version,
            SampleRate = first.SampleRate,
            Timestamp = sessions.Min(s => s.Timestamp),
            Linkage = first.Linkage,
            Front = new Suspension { Present = hasFront, Strokes = new Strokes() },
            Rear = new Suspension { Present = hasRear, Strokes = new Strokes() }
        };

        var smoother = new WhittakerHendersonSmoother(2, 5);

        if (hasFront)
        {
            combined.Front.Travel = ConcatenateTravelWithTransitions(
                sessions.Select(s => s.Front.Travel).ToList(), first.SampleRate);
            combined.Front.Calibration = first.Front.Calibration;

            var tbins = Linspace(0, first.Linkage.MaxFrontTravel, Parameters.TravelHistBins + 1);
            var dt = Digitize(combined.Front.Travel, tbins);
            combined.Front.TravelBins = tbins;

            // Re-derive velocity from combined travel to avoid discontinuities at session boundaries
            var v = ComputeVelocity(smoother.Smooth(combined.Front.Travel), first.SampleRate);
            combined.Front.Velocity = v;
            var (vbins, dv) = DigitizeVelocity(v, Parameters.VelocityHistStep);
            combined.Front.VelocityBins = vbins;
            var (vbinsFine, dvFine) = DigitizeVelocity(v, Parameters.VelocityHistStepFine);
            combined.Front.FineVelocityBins = vbinsFine;

            var strokes = Strokes.FilterStrokes(v, combined.Front.Travel,
                first.Linkage.MaxFrontTravel, first.SampleRate);
            combined.Front.Strokes.Categorize(strokes);
            if (combined.Front.Strokes.Compressions.Length == 0 && combined.Front.Strokes.Rebounds.Length == 0)
                combined.Front.Present = false;
            else
                combined.Front.Strokes.Digitize(dt, dv, dvFine);
        }

        if (hasRear)
        {
            combined.Rear.Travel = ConcatenateTravelWithTransitions(
                sessions.Select(s => s.Rear.Travel).ToList(), first.SampleRate);
            combined.Rear.Calibration = first.Rear.Calibration;

            var tbins = Linspace(0, first.Linkage.MaxRearTravel, Parameters.TravelHistBins + 1);
            var dt = Digitize(combined.Rear.Travel, tbins);
            combined.Rear.TravelBins = tbins;

            // Re-derive velocity from combined travel to avoid discontinuities at session boundaries
            var v = ComputeVelocity(smoother.Smooth(combined.Rear.Travel), first.SampleRate);
            combined.Rear.Velocity = v;
            var (vbins, dv) = DigitizeVelocity(v, Parameters.VelocityHistStep);
            combined.Rear.VelocityBins = vbins;
            var (vbinsFine, dvFine) = DigitizeVelocity(v, Parameters.VelocityHistStepFine);
            combined.Rear.FineVelocityBins = vbinsFine;

            var strokes = Strokes.FilterStrokes(v, combined.Rear.Travel,
                first.Linkage.MaxRearTravel, first.SampleRate);
            combined.Rear.Strokes.Categorize(strokes);
            if (combined.Rear.Strokes.Compressions.Length == 0 && combined.Rear.Strokes.Rebounds.Length == 0)
                combined.Rear.Present = false;
            else
                combined.Rear.Strokes.Digitize(dt, dv, dvFine);
        }

        combined.CalculateAirTimes();
        return combined;
    }

    #region Data calculations

    public HistogramData CalculateTravelHistogram(SuspensionType type)
    {
        var suspension = type == SuspensionType.Front ? Front : Rear;

        var hist = new double[suspension.TravelBins.Length - 1];
        var totalCount = 0;

        foreach (var s in suspension.Strokes.Compressions.Concat(suspension.Strokes.Rebounds))
        {
            totalCount += s.Stat.Count;
            foreach (var d in s.DigitizedTravel)
            {
                hist[d] += 1;
            }
        }

        hist = hist.Select(value => value / totalCount * 100.0).ToArray();

        return new HistogramData(
            suspension.TravelBins.ToList().GetRange(0, suspension.TravelBins.Length), [.. hist]);
    }

    public DetailedTravelHistogramData CalculateDetailedTravelHistogram(SuspensionType type, double? binSizeMm = null)
    {
        var suspension = type == SuspensionType.Front ? Front : Rear;
        var maxTravel = type == SuspensionType.Front ? Linkage.MaxFrontTravel : Linkage.MaxRearTravel;

        double[] travelBins;
        int histLen;
        double[] hist;
        var totalCount = 0.0;

        if (binSizeMm.HasValue && maxTravel > 0)
        {
            histLen = (int)Math.Ceiling(maxTravel / binSizeMm.Value);
            travelBins = new double[histLen + 1];
            for (var i = 0; i <= histLen; i++)
                travelBins[i] = i * binSizeMm.Value;

            hist = new double[histLen];
            foreach (var stroke in suspension.Strokes.Compressions.Concat(suspension.Strokes.Rebounds))
            {
                for (var i = stroke.Start; i <= stroke.End; i++)
                {
                    totalCount++;
                    var idx = Math.Min((int)(suspension.Travel[i] / binSizeMm.Value), histLen - 1);
                    if (idx >= 0) hist[idx] += 1;
                }
            }
        }
        else
        {
            histLen = Math.Max(0, suspension.TravelBins.Length - 1);
            travelBins = suspension.TravelBins;
            hist = new double[histLen];

            foreach (var stroke in suspension.Strokes.Compressions.Concat(suspension.Strokes.Rebounds))
            {
                totalCount += stroke.Stat.Count;
                foreach (var digitizedTravel in stroke.DigitizedTravel)
                {
                    if (digitizedTravel >= 0 && digitizedTravel < histLen)
                    {
                        hist[digitizedTravel] += 1;
                    }
                }
            }
        }

        if (totalCount > 0)
        {
            for (var i = 0; i < hist.Length; i++)
            {
                hist[i] = hist[i] / totalCount * 100.0;
            }
        }

        var midsMm = new List<double>(histLen);
        var midsPercentage = new List<double>(histLen);
        var widthsMm = new List<double>(histLen);
        var widthsPercentage = new List<double>(histLen);

        if (histLen > 0 && maxTravel > 0)
        {
            const double percentageGap = 0.75;
            var mmGap = percentageGap * maxTravel / 100.0;

            for (var i = 0; i < histLen; i++)
            {
                var left = travelBins[i];
                var right = travelBins[i + 1];
                var widthMm = right - left;
                var midMm = (left + right) / 2.0;

                var widthPercentage = widthMm / maxTravel * 100.0;
                var adjustedWidthPercentage = Math.Max(widthPercentage - percentageGap, widthPercentage * 0.1);
                var adjustedWidthMm = Math.Max(widthMm - mmGap, widthMm * 0.1);

                midsMm.Add(midMm);
                midsPercentage.Add(midMm / maxTravel * 100.0);
                widthsMm.Add(adjustedWidthMm);
                widthsPercentage.Add(adjustedWidthPercentage);
            }
        }

        return new DetailedTravelHistogramData(
            midsMm,
            midsPercentage,
            [.. hist],
            widthsMm,
            widthsPercentage,
            maxTravel);
    }

    public StackedHistogramData CalculateVelocityHistogram(SuspensionType type)
    {
        var suspension = type == SuspensionType.Front ? Front : Rear;

        var divider = (suspension.TravelBins.Length - 1) / TravelBinsForVelocityHistogram;
        var hist = new double[suspension.VelocityBins.Length - 1][];
        for (var i = 0; i < hist.Length; i++)
        {
            hist[i] = Generate.Zeros(TravelBinsForVelocityHistogram);
        }

        var totalCount = 0;
        foreach (var s in suspension.Strokes.Compressions.Concat(suspension.Strokes.Rebounds))
        {
            totalCount += s.Stat.Count;
            for (int i = 0; i < s.Stat.Count; ++i)
            {
                var vbin = s.DigitizedVelocity[i];
                var tbin = s.DigitizedTravel[i] / divider;
                hist[vbin][tbin] += 1;
            }
        }

        var largestBin = 0.0;
        foreach (var travelHist in hist)
        {
            var travelSum = 0.0;
            for (var j = 0; j < TravelBinsForVelocityHistogram; j++)
            {
                travelHist[j] = travelHist[j] / totalCount * 100.0;
                travelSum += travelHist[j];
            }

            largestBin = Math.Max(travelSum, largestBin);
        }

        return new StackedHistogramData(
            suspension.VelocityBins.ToList().GetRange(0, suspension.VelocityBins.Length), [.. hist]);
    }

    public StackedHistogramData CalculateLowSpeedVelocityHistogram(SuspensionType type, double highSpeedThreshold)
    {
        var suspension = type == SuspensionType.Front ? Front : Rear;

        var divider = (suspension.TravelBins.Length - 1) / TravelBinsForVelocityHistogram;
        var stepFine = suspension.FineVelocityBins[1] - suspension.FineVelocityBins[0];
        var hist = new double[suspension.FineVelocityBins.Length - 1][];
        for (var i = 0; i < hist.Length; i++)
        {
            hist[i] = Generate.Zeros(TravelBinsForVelocityHistogram);
        }

        var totalCount = 0;
        foreach (var s in suspension.Strokes.Compressions.Concat(suspension.Strokes.Rebounds))
        {
            totalCount += s.Stat.Count;
            for (int i = 0; i < s.Stat.Count; ++i)
            {
                var vbinFine = s.FineDigitizedVelocity[i];
                var midpoint = suspension.FineVelocityBins[vbinFine] + stepFine / 2.0;
                if (midpoint <= -(highSpeedThreshold + stepFine / 2.0) ||
                    midpoint >= (highSpeedThreshold + stepFine / 2.0))
                    continue;

                var tbin = s.DigitizedTravel[i] / divider;
                hist[vbinFine][tbin] += 1;
            }
        }

        if (totalCount > 0)
        {
            foreach (var travelHist in hist)
            {
                for (var j = 0; j < TravelBinsForVelocityHistogram; j++)
                {
                    travelHist[j] = travelHist[j] / totalCount * 100.0;
                }
            }
        }

        return new StackedHistogramData(
            suspension.FineVelocityBins.ToList().GetRange(0, suspension.FineVelocityBins.Length), [.. hist]);
    }

    public static double CalculateVelocityHistogramSymmetry(StackedHistogramData histogram)
    {
        if (histogram.Bins.Count < 2 || histogram.Values.Count == 0)
            return 0.0;

        var step = histogram.Bins[1] - histogram.Bins[0];
        if (Math.Abs(step) < 1e-9)
            return 0.0;

        var totalsByHalfStepKey = new Dictionary<long, double>(histogram.Values.Count);
        for (var i = 0; i < histogram.Values.Count; i++)
        {
            var total = histogram.Values[i].Sum();
            var midpoint = histogram.Bins[i] + step / 2.0;
            var key = (long)Math.Round(midpoint / step * 2.0);
            totalsByHalfStepKey[key] = total;
        }

        double numerator = 0.0;
        double denominator = 0.0;
        var processed = new HashSet<long>();
        foreach (var key in totalsByHalfStepKey.Keys)
        {
            var absKey = Math.Abs(key);
            if (absKey == 0 || processed.Contains(absKey))
                continue;

            processed.Add(absKey);
            var positive = totalsByHalfStepKey.GetValueOrDefault(absKey, 0.0);
            var negative = totalsByHalfStepKey.GetValueOrDefault(-absKey, 0.0);
            numerator += Math.Abs(positive - negative);
            denominator += positive + negative;
        }

        if (denominator <= 1e-9)
            return 0.0;

        var symmetry = 1.0 - numerator / denominator;
        return Math.Clamp(symmetry, 0.0, 1.0);
    }

    public NormalDistributionData CalculateNormalDistribution(SuspensionType type)
    {
        var suspension = type == SuspensionType.Front ? Front : Rear;
        var step = suspension.VelocityBins[1] - suspension.VelocityBins[0];

        // Welford's online algorithm — avoids allocating a list of all velocity samples
        long n = 0;
        double mean = 0.0, m2 = 0.0;
        double min = double.MaxValue, max = double.MinValue;

        foreach (var s in suspension.Strokes.Compressions.Concat(suspension.Strokes.Rebounds))
        {
            if (s.End < s.Start || s.Start < 0 || s.End >= suspension.Velocity.Length) continue;
            for (var i = s.Start; i <= s.End; i++)
            {
                var v = suspension.Velocity[i];
                n++;
                var delta = v - mean;
                mean += delta / n;
                m2 += delta * (v - mean);
                if (v < min) min = v;
                if (v > max) max = v;
            }
        }

        if (n < 2)
            return new NormalDistributionData([], []);

        var mu = mean;
        var std = Math.Sqrt(m2 / (n - 1));

        var range = max - min;
        var ny = new double[100];
        for (int i = 0; i < 100; i++)
        {
            ny[i] = min + i * range / 99;
        }

        var pdf = new List<double>(100);
        for (int i = 0; i < 100; i++)
        {
            pdf.Add(Normal.PDF(mu, std, ny[i]) * step * 100);
        }

        return new NormalDistributionData([.. ny], pdf);
    }

    /// <summary>
    /// Normal distribution for the low-speed velocity histogram.
    /// Uses fine velocity bin step and population std (matching Python norm.fit / MLE).
    /// Y values are in mm/s (not converted to m/s).
    /// </summary>
    public NormalDistributionData CalculateLowSpeedNormalDistribution(SuspensionType type, double highSpeedThreshold)
    {
        var suspension = type == SuspensionType.Front ? Front : Rear;
        var fineStep = suspension.FineVelocityBins[1] - suspension.FineVelocityBins[0];

        // Welford's online algorithm — avoids allocating a list of all velocity samples
        long n = 0;
        double mean = 0.0, m2 = 0.0;

        foreach (var s in suspension.Strokes.Compressions.Concat(suspension.Strokes.Rebounds))
        {
            if (s.End < s.Start || s.Start < 0 || s.End >= suspension.Velocity.Length) continue;
            for (var i = s.Start; i <= s.End; i++)
            {
                var v = suspension.Velocity[i];
                n++;
                var delta = v - mean;
                mean += delta / n;
                m2 += delta * (v - mean);
            }
        }

        if (n < 1)
            return new NormalDistributionData([], []);

        var mu = mean;
        var std = Math.Sqrt(m2 / n); // population std

        var limit = highSpeedThreshold + 50; // match the plot display range (velocityLimit)
        const int numPoints = 100;
        var ny = new double[numPoints];
        for (int i = 0; i < numPoints; i++)
        {
            ny[i] = -limit + i * (2.0 * limit) / (numPoints - 1);
        }

        var pdf = new List<double>(numPoints);
        for (int i = 0; i < numPoints; i++)
        {
            pdf.Add(Normal.PDF(mu, std, ny[i]) * fineStep * 100);
        }

        return new NormalDistributionData([.. ny], pdf);
    }

    public TravelStatistics CalculateTravelStatistics(SuspensionType type)
    {
        var suspension = type == SuspensionType.Front ? Front : Rear;

        var sum = 0.0;
        var count = 0.0;
        var mx = 0.0;
        var bo = 0;

        foreach (var stroke in suspension.Strokes.Compressions.Concat(suspension.Strokes.Rebounds))
        {
            sum += stroke.Stat.SumTravel;
            count += stroke.Stat.Count;
            bo += stroke.Stat.Bottomouts;
            if (stroke.Stat.MaxTravel > mx)
            {
                mx = stroke.Stat.MaxTravel;
            }
        }

        return new TravelStatistics(mx, sum / count, bo);
    }

    public DetailedTravelStatistics CalculateDetailedTravelStatistics(SuspensionType type)
    {
        var suspension = type == SuspensionType.Front ? Front : Rear;
        var travelValues = new List<double>();

        var bottomouts = 0;
        foreach (var stroke in suspension.Strokes.Compressions.Concat(suspension.Strokes.Rebounds))
        {
            bottomouts += stroke.Stat.Bottomouts;

            if (stroke.End < stroke.Start || stroke.Start < 0 || stroke.End >= suspension.Travel.Length)
            {
                continue;
            }

            for (var i = stroke.Start; i <= stroke.End; i++)
            {
                travelValues.Add(suspension.Travel[i]);
            }
        }

        if (travelValues.Count == 0)
        {
            return new DetailedTravelStatistics(0.0, 0.0, 0.0, 0);
        }

        var average = travelValues.Average();
        var max = travelValues.Max();
        var p95 = travelValues.Percentile(95);

        return new DetailedTravelStatistics(max, average, p95, bottomouts);
    }

    public VelocityStatistics CalculateVelocityStatistics(SuspensionType type)
    {
        var suspension = type == SuspensionType.Front ? Front : Rear;

        var csum = 0.0;
        var ccount = 0.0;
        var maxc = 0.0;
        foreach (var compression in suspension.Strokes.Compressions)
        {
            csum += compression.Stat.SumVelocity;
            ccount += compression.Stat.Count;
            if (compression.Stat.MaxVelocity > maxc)
            {
                maxc = compression.Stat.MaxVelocity;
            }
        }
        var rsum = 0.0;
        var rcount = 0.0;
        var maxr = 0.0;
        foreach (var rebound in suspension.Strokes.Rebounds)
        {
            rsum += rebound.Stat.SumVelocity;
            rcount += rebound.Stat.Count;
            if (rebound.Stat.MaxVelocity < maxr)
            {
                maxr = rebound.Stat.MaxVelocity;
            }
        }

        return new VelocityStatistics(
            rsum / rcount,
            maxr,
            csum / ccount,
            maxc);
    }

    public VelocityBands CalculateVelocityBands(SuspensionType type, double highSpeedThreshold)
    {
        var suspension = type == SuspensionType.Front ? Front : Rear;
        var velocity = suspension.Velocity;

        var totalCount = 0.0;
        var lsc = 0.0;
        var hsc = 0.0;

        // Process compressions
        foreach (var compression in suspension.Strokes.Compressions)
        {
            if (compression.End < compression.Start || compression.Start < 0 || compression.End >= velocity.Length)
                continue;
            totalCount += compression.Stat.Count;
            for (int i = compression.Start; i <= compression.End; i++)
            {
                if (velocity[i] < highSpeedThreshold)
                {
                    lsc++;
                }
                else
                {
                    hsc++;
                }
            }
        }

        var lsr = 0.0;
        var hsr = 0.0;

        // Process rebounds
        foreach (var rebound in suspension.Strokes.Rebounds)
        {
            if (rebound.End < rebound.Start || rebound.Start < 0 || rebound.End >= velocity.Length)
                continue;
            totalCount += rebound.Stat.Count;
            for (int i = rebound.Start; i <= rebound.End; i++)
            {
                if (velocity[i] > -highSpeedThreshold)
                {
                    lsr++;
                }
                else
                {
                    hsr++;
                }
            }
        }

        if (totalCount == 0)
            return new VelocityBands(0, 0, 0, 0);

        var totalPercentage = 100.0 / totalCount;
        return new VelocityBands(
            lsc * totalPercentage,
            hsc * totalPercentage,
            lsr * totalPercentage,
            hsr * totalPercentage);
    }

    public PositionVelocityData CalculatePositionVelocityData(SuspensionType type)
    {
        var suspension = type == SuspensionType.Front ? Front : Rear;
        var arrayLen = Math.Min(suspension.Travel.Length, suspension.Velocity.Length);
        var travel = new List<double>();
        var velocity = new List<double>();

        foreach (var s in suspension.Strokes.Compressions.Concat(suspension.Strokes.Rebounds))
        {
            if (s.End < s.Start || s.Start < 0 || s.End >= arrayLen) continue;
            for (var i = s.Start; i <= s.End; i++)
            {
                travel.Add(suspension.Travel[i]);
                velocity.Add(suspension.Velocity[i]);
            }
        }

        return new PositionVelocityData(travel.ToArray(), velocity.ToArray());
    }

    public PositionVelocityData CalculateDamperPositionVelocityData()
    {
        var arrayLen = Math.Min(Rear.Travel.Length, Rear.Velocity.Length);
        var travel = new List<double>();
        var velocity = new List<double>();

        foreach (var s in Rear.Strokes.Compressions.Concat(Rear.Strokes.Rebounds))
        {
            if (s.End < s.Start || s.Start < 0 || s.End >= arrayLen) continue;
            for (var i = s.Start; i <= s.End; i++)
            {
                travel.Add(Linkage.WheelToDamperTravel(Rear.Travel[i]));
                velocity.Add(Rear.Velocity[i]);
            }
        }

        return new PositionVelocityData(travel.ToArray(), velocity.ToArray());
    }

    public PositionVelocityData CalculateForkPositionVelocityData()
    {
        var sinHeadAngle = Math.Sin(Linkage.HeadAngle * Math.PI / 180.0);
        var arrayLen = Math.Min(Front.Travel.Length, Front.Velocity.Length);
        var travel = new List<double>();
        var velocity = new List<double>();

        foreach (var s in Front.Strokes.Compressions.Concat(Front.Strokes.Rebounds))
        {
            if (s.End < s.Start || s.Start < 0 || s.End >= arrayLen) continue;
            for (var i = s.Start; i <= s.End; i++)
            {
                travel.Add(sinHeadAngle > 0 ? Front.Travel[i] / sinHeadAngle : 0);
                velocity.Add(Front.Velocity[i]);
            }
        }

        return new PositionVelocityData(travel.ToArray(), velocity.ToArray());
    }

    private static Func<double, double> FitPolynomial(double[] x, double[] y)
    {
        var coefficients = Fit.Polynomial(x, y, 1);
        return t => coefficients[1] * t + coefficients[0];
    }

    private (double[], double[]) TravelVelocity(SuspensionType suspensionType, BalanceType balanceType)
    {
        var suspension = suspensionType == SuspensionType.Front ? Front : Rear;
        var travelMax = suspensionType == SuspensionType.Front ? Linkage.MaxFrontTravel : Linkage.MaxRearTravel;
        var strokes = balanceType == BalanceType.Compression
            ? suspension.Strokes.Compressions
            : suspension.Strokes.Rebounds;

        var t = new List<double>();
        var v = new List<double>();

        foreach (var s in strokes)
        {
            t.Add(s.Stat.MaxTravel / travelMax * 100);

            // Rebound velocities are negative, compression velocities are positive.
            v.Add(s.Stat.MaxVelocity);
        }

        var tArray = t.ToArray();
        var vArray = v.ToArray();

        Array.Sort(tArray, vArray);

        return (tArray, vArray);
    }

    public BalanceData CalculateBalance(BalanceType type)
    {
        var frontTravelVelocity = TravelVelocity(SuspensionType.Front, type);
        var rearTravelVelocity = TravelVelocity(SuspensionType.Rear, type);

        var frontPoly = FitPolynomial(frontTravelVelocity.Item1, frontTravelVelocity.Item2);
        var rearPoly = FitPolynomial(rearTravelVelocity.Item1, rearTravelVelocity.Item2);

        var evalPoints = Enumerable.Range(0, 100).Select(i => i + 0.5).ToArray();
        var sum = evalPoints.Sum(t => frontPoly(t) - rearPoly(t));
        var msd = sum / evalPoints.Length;

        return new BalanceData(
            [.. frontTravelVelocity.Item1],
            [.. frontTravelVelocity.Item2],
            frontTravelVelocity.Item1.Select(t => frontPoly(t)).ToList(),
            [.. rearTravelVelocity.Item1],
            [.. rearTravelVelocity.Item2],
            rearTravelVelocity.Item1.Select(t => rearPoly(t)).ToList(),
            msd);
    }

    #endregion

    #region Spectrum / balance metrics

    // Welch's method: Hanning window, 50% overlap, averaged across segments.
    // Returns (frequencies in Hz, single-sided amplitude in mm). Empty arrays if signal is too short.
    public static TravelSpectrum ComputeWelchSpectrum(double[] signal, int sampleRate, int segLen = 8192)
    {
        if (signal == null || sampleRate <= 0) return new TravelSpectrum([], []);

        int n = signal.Length;
        if (segLen > n) segLen = n;
        if ((segLen & 1) != 0) segLen--;
        if (segLen < 64) return new TravelSpectrum([], []);

        var window = Window.Hann(segLen);
        double winSum = 0;
        for (int i = 0; i < segLen; i++) winSum += window[i];

        int step = segLen / 2;
        int bins = segLen / 2;
        var avgPower = new double[bins];
        int segCount = 0;
        var buffer = new Complex[segLen];

        for (int start = 0; start + segLen <= n; start += step)
        {
            double mean = 0;
            for (int i = 0; i < segLen; i++) mean += signal[start + i];
            mean /= segLen;

            for (int i = 0; i < segLen; i++)
                buffer[i] = new Complex((signal[start + i] - mean) * window[i], 0);

            Fourier.Forward(buffer, FourierOptions.NoScaling);

            for (int k = 0; k < bins; k++)
            {
                var m = buffer[k].Magnitude;
                avgPower[k] += m * m;
            }
            segCount++;
        }

        if (segCount == 0) return new TravelSpectrum([], []);

        double powerScale = 2.0 / (winSum * winSum);
        double dF = (double)sampleRate / segLen;

        var freqs = new double[bins];
        var amps = new double[bins];
        for (int k = 0; k < bins; k++)
        {
            freqs[k] = k * dF;
            var power = (avgPower[k] / segCount) * powerScale;
            amps[k] = Math.Sqrt(power);
        }
        return new TravelSpectrum(freqs, amps);
    }

    // Welch cross-spectrum + per-axis auto-spectra computed in a single pass with the
    // same windowing/segmentation as ComputeWelchSpectrum. Returns averaged Pxx, Pyy
    // (real, single-sided power) and the complex cross spectrum Pxy. Empty arrays if
    // signals are too short or mismatched.
    public static (double[] Freqs, double[] Pxx, double[] Pyy, Complex[] Pxy) ComputeWelchCrossSpectrum(
        double[] x, double[] y, int sampleRate, int segLen = 8192)
    {
        if (x == null || y == null || sampleRate <= 0 || x.Length != y.Length)
            return ([], [], [], []);

        int n = x.Length;
        if (segLen > n) segLen = n;
        if ((segLen & 1) != 0) segLen--;
        if (segLen < 64) return ([], [], [], []);

        var window = Window.Hann(segLen);
        double winSum = 0;
        for (int i = 0; i < segLen; i++) winSum += window[i];

        int step = segLen / 2;
        int bins = segLen / 2;
        var pxx = new double[bins];
        var pyy = new double[bins];
        var pxy = new Complex[bins];
        int segCount = 0;
        var bx = new Complex[segLen];
        var by = new Complex[segLen];

        for (int start = 0; start + segLen <= n; start += step)
        {
            double mx = 0, my = 0;
            for (int i = 0; i < segLen; i++) { mx += x[start + i]; my += y[start + i]; }
            mx /= segLen; my /= segLen;

            for (int i = 0; i < segLen; i++)
            {
                bx[i] = new Complex((x[start + i] - mx) * window[i], 0);
                by[i] = new Complex((y[start + i] - my) * window[i], 0);
            }
            Fourier.Forward(bx, FourierOptions.NoScaling);
            Fourier.Forward(by, FourierOptions.NoScaling);

            for (int k = 0; k < bins; k++)
            {
                pxx[k] += bx[k].Magnitude * bx[k].Magnitude;
                pyy[k] += by[k].Magnitude * by[k].Magnitude;
                pxy[k] += bx[k] * Complex.Conjugate(by[k]);
            }
            segCount++;
        }
        if (segCount == 0) return ([], [], [], []);

        double powerScale = 2.0 / (winSum * winSum);
        double dF = (double)sampleRate / segLen;
        var freqs = new double[bins];
        for (int k = 0; k < bins; k++)
        {
            freqs[k] = k * dF;
            pxx[k] = pxx[k] / segCount * powerScale;
            pyy[k] = pyy[k] / segCount * powerScale;
            pxy[k] = pxy[k] / segCount * powerScale;
        }
        return (freqs, pxx, pyy, pxy);
    }

    // Trapezoidal integration of a single-sided spectrum (e.g. Pxx) over [fLow, fHigh].
    public static double IntegrateBand(double[] freqs, double[] spectrum, double fLow, double fHigh)
    {
        if (freqs.Length < 2 || spectrum.Length != freqs.Length) return 0;
        double sum = 0;
        for (int i = 1; i < freqs.Length; i++)
        {
            double f0 = freqs[i - 1], f1 = freqs[i];
            if (f1 < fLow || f0 > fHigh) continue;
            double a = Math.Max(f0, fLow);
            double b = Math.Min(f1, fHigh);
            if (b <= a) continue;
            // linear interpolation of spectrum at [a, b]
            double s0 = spectrum[i - 1] + (spectrum[i] - spectrum[i - 1]) * (a - f0) / (f1 - f0);
            double s1 = spectrum[i - 1] + (spectrum[i] - spectrum[i - 1]) * (b - f0) / (f1 - f0);
            sum += 0.5 * (s0 + s1) * (b - a);
        }
        return sum;
    }

    // Mean magnitude-squared coherence over [fLow, fHigh].
    // γ²(f) = |Pxy|² / (Pxx · Pyy). Skips DC and uses bin centers within range.
    public static double? MeanCoherence(
        double[] freqs, double[] pxx, double[] pyy, Complex[] pxy, double fLow, double fHigh)
    {
        if (freqs.Length == 0 || pxy.Length != freqs.Length) return null;
        double sum = 0;
        int n = 0;
        for (int k = 1; k < freqs.Length; k++)
        {
            double f = freqs[k];
            if (f < fLow || f > fHigh) continue;
            double denom = pxx[k] * pyy[k];
            if (denom <= 1e-30) continue;
            double mag2 = pxy[k].Real * pxy[k].Real + pxy[k].Imaginary * pxy[k].Imaginary;
            double g2 = mag2 / denom;
            if (g2 > 1.0) g2 = 1.0;
            sum += g2;
            n++;
        }
        return n > 0 ? sum / n : (double?)null;
    }

    // Discipline-aware Low/Mid split frequency. High band starts fix at 8 Hz.
    public static double FrequencySplitFor(Discipline? d) => d switch
    {
        Discipline.XC       => 2.8,
        Discipline.Downhill => 1.6,
        _                   => 2.0, // Enduro / default
    };

    // Body-resonance peak detection in the velocity domain.
    //
    // The travel-amplitude spectrum is dominated by a 1/f-like trend, so finding
    // the resonance there requires detrending and threshold heuristics that are
    // fragile across sessions. Multiplying amplitude by 2πf converts to the
    // velocity-amplitude spectrum, where the trend is removed and the body
    // resonance is structurally the dominant feature. We pick the absolute max
    // of the velocity spectrum in [fMin, fMax]; the returned amplitude is the
    // original travel amplitude at that bin (so plot markers sit on the travel
    // curve).
    public static (double Frequency, double Amplitude) FindDominantPeak(
        TravelSpectrum spectrum, double fMin, double fMax)
    {
        var freqs = spectrum.Frequencies;
        var amps = spectrum.Amplitudes;
        int n = amps.Length;
        if (n < 2) return (double.NaN, 0);

        int bestK = -1;
        double bestVel = 0;
        for (int i = 1; i < n; i++)
        {
            double f = freqs[i];
            if (f < fMin || f > fMax) continue;
            double vel = amps[i] * 2.0 * Math.PI * f;
            if (vel > bestVel)
            {
                bestVel = vel;
                bestK = i;
            }
        }

        if (bestK < 0) return (double.NaN, 0);

        double bestF = freqs[bestK];
        double bestA = amps[bestK];

        // Quadratic peak interpolation in velocity-dB domain. The Hann window
        // used by ComputeWelchSpectrum has a worst-case scalloping loss of
        // ~1.4 dB when the true resonance frequency lies between two bins;
        // fitting a parabola through the three bins around the maximum and
        // returning the vertex recovers sub-bin accuracy in both frequency
        // and amplitude (residual error < 0.1 dB). Standard technique in
        // audio / vibration analysis.
        if (bestK > 0 && bestK < n - 1)
        {
            // Velocity at the three bins (peak detection runs in velocity
            // domain, so the parabola must be fitted in the same domain).
            double v0 = amps[bestK - 1] * 2.0 * Math.PI * freqs[bestK - 1];
            double v1 = bestVel;
            double v2 = amps[bestK + 1] * 2.0 * Math.PI * freqs[bestK + 1];
            if (v0 > 0 && v1 > 0 && v2 > 0)
            {
                double alpha = 20.0 * Math.Log10(v0);
                double beta  = 20.0 * Math.Log10(v1);
                double gamma = 20.0 * Math.Log10(v2);
                double denom = alpha - 2.0 * beta + gamma;
                // denom < 0 confirms the parabola opens downward (true local max)
                if (denom < 0)
                {
                    double delta = 0.5 * (alpha - gamma) / denom;
                    if (delta > -0.5 && delta < 0.5)
                    {
                        double dF = freqs[bestK + 1] - freqs[bestK]; // bin width (constant)
                        bestF = freqs[bestK] + delta * dF;
                        double peakVelDb = beta - 0.25 * (alpha - gamma) * delta;
                        double peakVel = Math.Pow(10.0, peakVelDb / 20.0);
                        // Convert back to travel amplitude (the API contract
                        // — callers multiply by 2π·f to get velocity).
                        bestA = peakVel / (2.0 * Math.PI * bestF);
                    }
                }
            }
        }

        return (bestF, bestA);
    }

    public BalanceMetrics CalculateBalanceMetrics(
        Discipline? discipline = null,
        IForceEstimator? forceEstimator = null,
        Setup? setup = null,
        Session? session = null)
    {
        double? fSag = null, rSag = null, sagDiff = null;
        double? fP95Pct = null, rP95Pct = null;
        int? fBO = null, rBO = null;
        double? compRatio = null, rebRatio = null;
        double? compMsd = null, rebMsd = null;
        double? fPeak = null, rPeak = null, fAmp = null, rAmp = null;
        double? freqDiff = null, ampRatio = null;
        double? lowEnergyDb = null, midEnergyDb = null, wheelEnergyDb = null, highEnergyDb = null;
        double? lowCoh = null, midCoh = null, wheelCoh = null, highCoh = null;
        double fSplit = FrequencySplitFor(discipline);

        // Wheel-load metrics (V1, dyno-spring-based estimator). All null when the estimator
        // is missing inputs (no spring component assigned, unparsable spring rate, etc.).
        double? fStaticN = null, rStaticN = null;
        double? fStaticFlatN = null, rStaticFlatN = null;
        double? fUnloadPct = null, rUnloadPct = null, unloadDiff = null;
        int? fUnloadEvents = null, rUnloadEvents = null;
        double? lowLoadM = null, midLoadM = null, wheelLoadM = null, highLoadM = null;
        double? fDynRangeFactor = null, rDynRangeFactor = null, dynRangeDiff = null;

        var maxF = Linkage?.MaxFrontTravel ?? 0;
        var maxR = Linkage?.MaxRearTravel ?? 0;

        if (Front.Present)
        {
            var ts = CalculateDetailedTravelStatistics(SuspensionType.Front);
            if (maxF > 0)
            {
                fSag = ts.Average / maxF * 100.0;
                fP95Pct = ts.P95 / maxF * 100.0;
            }
            fBO = ts.Bottomouts;
        }
        if (Rear.Present)
        {
            var ts = CalculateDetailedTravelStatistics(SuspensionType.Rear);
            if (maxR > 0)
            {
                rSag = ts.Average / maxR * 100.0;
                rP95Pct = ts.P95 / maxR * 100.0;
            }
            rBO = ts.Bottomouts;
        }
        if (fSag.HasValue && rSag.HasValue)
            sagDiff = Math.Abs(fSag.Value - rSag.Value);

        if (Front.Present && Rear.Present)
        {
            var fv = CalculateVelocityStatistics(SuspensionType.Front);
            var rv = CalculateVelocityStatistics(SuspensionType.Rear);
            // Michelson-style imbalance index (F-R)/(F+R) — bounded in (-1, +1),
            // 0 = perfect balance, positive = front dominates, negative = rear dominates.
            var fc = fv.AverageCompression;
            var rc = rv.AverageCompression;
            if (fc + rc > 1e-6)
                compRatio = (fc - rc) / (fc + rc);
            var fr = Math.Abs(fv.AverageRebound);
            var rr = Math.Abs(rv.AverageRebound);
            if (fr + rr > 1e-6)
                rebRatio = (fr - rr) / (fr + rr);

            // Normalize MSD by the larger of front/rear peak velocity (matches BalancePlot's
            // on-plot label, which is in % of full-scale velocity, not raw mm/s).
            try
            {
                var b = CalculateBalance(BalanceType.Compression);
                var mv = Math.Max(
                    b.FrontVelocity.Select(Math.Abs).DefaultIfEmpty(0).Max(),
                    b.RearVelocity.Select(Math.Abs).DefaultIfEmpty(0).Max());
                if (mv > 1e-6) compMsd = b.MeanSignedDeviation / mv * 100.0;
            }
            catch { /* not enough strokes for polynomial fit */ }
            try
            {
                var b = CalculateBalance(BalanceType.Rebound);
                var mv = Math.Max(
                    b.FrontVelocity.Select(Math.Abs).DefaultIfEmpty(0).Max(),
                    b.RearVelocity.Select(Math.Abs).DefaultIfEmpty(0).Max());
                if (mv > 1e-6) rebMsd = b.MeanSignedDeviation / mv * 100.0;
            }
            catch { /* not enough strokes for polynomial fit */ }
        }

        if (Front.Present && Front.Travel != null && Front.Travel.Length >= 8192)
        {
            var spec = ComputeWelchSpectrum(Front.Travel, SampleRate);
            if (spec.Amplitudes.Length > 0)
            {
                var (f, a) = FindDominantPeak(spec, 1.3, 4.5);
                if (!double.IsNaN(f)) { fPeak = f; fAmp = a; }
            }
        }
        if (Rear.Present && Rear.Travel != null && Rear.Travel.Length >= 8192)
        {
            var spec = ComputeWelchSpectrum(Rear.Travel, SampleRate);
            if (spec.Amplitudes.Length > 0)
            {
                var (f, a) = FindDominantPeak(spec, 1.3, 4.5);
                if (!double.IsNaN(f)) { rPeak = f; rAmp = a; }
            }
        }
        if (fPeak.HasValue && rPeak.HasValue)
            freqDiff = Math.Abs(fPeak.Value - rPeak.Value);
        // Michelson-style imbalance index, same convention as the velocity ratios.
        if (fAmp.HasValue && rAmp.HasValue && fAmp.Value + rAmp.Value > 1e-9)
            ampRatio = (fAmp.Value - rAmp.Value) / (fAmp.Value + rAmp.Value);

        // Front/Rear frequency-balance: per-band energy ratio (dB) + magnitude-squared
        // coherence γ²(f). Four bands isolating distinct physical regimes:
        //   Low   [1.0, fSplit]  body / sprung-mass resonance
        //   Mid   [fSplit, 10]   transition / frame transmission
        //   Wheel [10, 25]       unsprung-mass / tire-stiffness resonance
        //   High  [25, 50]       above-resonance noise (spoke modes, drivetrain, frame chatter)
        if (Front.Present && Rear.Present
            && Front.Travel != null && Rear.Travel != null
            && Front.Travel.Length >= 8192 && Rear.Travel.Length >= 8192
            && Front.Travel.Length == Rear.Travel.Length)
        {
            var (cf, pxx, pyy, pxy) = ComputeWelchCrossSpectrum(Front.Travel, Rear.Travel, SampleRate);
            if (cf.Length > 0)
            {
                static double? RatioDb(double[] freqs, double[] f, double[] r, double lo, double hi)
                {
                    var ef = IntegrateBand(freqs, f, lo, hi);
                    var er = IntegrateBand(freqs, r, lo, hi);
                    if (ef <= 0 || er <= 0) return null;
                    return 10.0 * Math.Log10(ef / er);
                }
                lowEnergyDb   = RatioDb(cf, pxx, pyy, 1.0,    fSplit);
                midEnergyDb   = RatioDb(cf, pxx, pyy, fSplit, 10.0);
                wheelEnergyDb = RatioDb(cf, pxx, pyy, 10.0,   25.0);
                highEnergyDb  = RatioDb(cf, pxx, pyy, 25.0,   50.0);

                lowCoh   = MeanCoherence(cf, pxx, pyy, pxy, 1.0,    fSplit);
                midCoh   = MeanCoherence(cf, pxx, pyy, pxy, fSplit, 10.0);
                wheelCoh = MeanCoherence(cf, pxx, pyy, pxy, 10.0,   25.0);
                highCoh  = MeanCoherence(cf, pxx, pyy, pxy, 25.0,   50.0);
            }
        }

        // Wheel-load force metrics. Active only when caller supplied an estimator + setup + session.
        if (forceEstimator is not null && setup is not null && session is not null)
        {
            ForceData? fForce = null, rForce = null;
            try { fForce = forceEstimator.Estimate(SuspensionType.Front, this, setup, session); } catch { }
            try { rForce = forceEstimator.Estimate(SuspensionType.Rear,  this, setup, session); } catch { }

            // Per-axis time-domain metrics. Tightened from the initial defaults after real-trail
            // sessions produced unrealistically high event counts (>30 on a moderate trail):
            //   - threshold 30 % → 20 %: 30 % is "less loaded than usual", not actual grip loss.
            //     Real traction loss on a tyre starts well below ~25 % of static load.
            //   - dwell 50 ms → 100 ms: 50 ms is in the same range as pedalling impulses and
            //     short root-bumps; only sustained dips deserve to count as a setup-relevant event.
            //   - Schmitt-trigger hysteresis (enter at 20 %, exit at 30 %): a single sustained
            //     unloading phase often shows small force spikes back above the entry threshold
            //     when the spring vibrates on a rough patch. Without hysteresis those spikes
            //     fragment one real event into several spurious ones. Exit-on-30 % bridges them
            //     while still demanding clear re-loading before counting the next event.
            int minSamples = Math.Max(1, (int)(0.10 * SampleRate));  // 100 ms minimum dwell for an "event"
            const double UnloadEnterFraction = 0.20;                  // F_wheel < 20% F_static = event start
            const double UnloadExitFraction  = 0.30;                  // F_wheel > 30% F_static = event end

            // Airtime mask — exclude periods where the whole bike is airborne. Both wheels
            // are unloaded by definition then; counting that as an unloading event would
            // pollute the metric and obscure setup-driven (asymmetric) unloading on rough ground.
            // The session-level Airtimes[] array uses a strict top-out criterion (travel ≤ 3 mm)
            // that real-world dampers often miss because of internal friction / negative-spring
            // equilibrium; we detect airtime here from the motion signal directly, using a
            // travel threshold relative to the running median (dyn-sag proxy).
            int forceLen = fForce?.WheelForce.Length ?? rForce?.WheelForce.Length ?? 0;
            var airMask = BuildAirtimeMaskFromMotion(forceLen, SampleRate, Front, Rear);

            if (fForce is not null && fForce.WheelForce.Length > 0 && fForce.StaticForce > 0)
            {
                fStaticN = fForce.StaticForce;
                fStaticFlatN = fForce.StaticForceFlat;
                var (pct, ev) = UnloadingStats(fForce.WheelForce,
                    UnloadEnterFraction * fForce.StaticForce,
                    UnloadExitFraction  * fForce.StaticForce,
                    minSamples, airMask);
                fUnloadPct = pct;
                fUnloadEvents = ev;
                fDynRangeFactor = DynamicRangeFactor(fForce.WheelForce, fForce.StaticForce);
            }
            if (rForce is not null && rForce.WheelForce.Length > 0 && rForce.StaticForce > 0)
            {
                rStaticN = rForce.StaticForce;
                rStaticFlatN = rForce.StaticForceFlat;
                var (pct, ev) = UnloadingStats(rForce.WheelForce,
                    UnloadEnterFraction * rForce.StaticForce,
                    UnloadExitFraction  * rForce.StaticForce,
                    minSamples, airMask);
                rUnloadPct = pct;
                rUnloadEvents = ev;
                rDynRangeFactor = DynamicRangeFactor(rForce.WheelForce, rForce.StaticForce);
            }
            if (fUnloadPct.HasValue && rUnloadPct.HasValue)
                unloadDiff = Math.Abs(fUnloadPct.Value - rUnloadPct.Value);
            if (fDynRangeFactor.HasValue && rDynRangeFactor.HasValue)
                dynRangeDiff = Math.Abs(fDynRangeFactor.Value - rDynRangeFactor.Value);

            // Frequency-domain Load F/R (statically-normalised Michelson).
            if (fForce is not null && rForce is not null
                && fForce.WheelForce.Length >= 8192 && rForce.WheelForce.Length >= 8192
                && fForce.WheelForce.Length == rForce.WheelForce.Length
                && fForce.StaticForce > 0 && rForce.StaticForce > 0)
            {
                var (ff, fpxx, fpyy, _) = ComputeWelchCrossSpectrum(fForce.WheelForce, rForce.WheelForce, SampleRate);
                if (ff.Length > 0)
                {
                    lowLoadM   = LoadMichelson(ff, fpxx, fpyy, fForce.StaticForce, rForce.StaticForce, 1.0,    fSplit);
                    midLoadM   = LoadMichelson(ff, fpxx, fpyy, fForce.StaticForce, rForce.StaticForce, fSplit, 10.0);
                    wheelLoadM = LoadMichelson(ff, fpxx, fpyy, fForce.StaticForce, rForce.StaticForce, 10.0,   25.0);
                    highLoadM  = LoadMichelson(ff, fpxx, fpyy, fForce.StaticForce, rForce.StaticForce, 25.0,   50.0);
                }
            }
        }

        return new BalanceMetrics(
            fSag, rSag, sagDiff,
            fP95Pct, rP95Pct,
            fBO, rBO,
            compRatio, rebRatio,
            compMsd, rebMsd,
            fPeak, rPeak,
            freqDiff, ampRatio,
            lowEnergyDb, midEnergyDb, wheelEnergyDb, highEnergyDb,
            lowCoh, midCoh, wheelCoh, highCoh,
            fSplit,
            FrontStaticForceN: fStaticN, RearStaticForceN: rStaticN,
            FrontStaticForceFlatN: fStaticFlatN, RearStaticForceFlatN: rStaticFlatN,
            FrontUnloadingPct: fUnloadPct, RearUnloadingPct: rUnloadPct,
            UnloadingDifferencePp: unloadDiff,
            FrontUnloadingEvents: fUnloadEvents, RearUnloadingEvents: rUnloadEvents,
            LowLoadMichelson: lowLoadM, MidLoadMichelson: midLoadM,
            WheelLoadMichelson: wheelLoadM, HighLoadMichelson: highLoadM,
            FrontDynamicRangeFactor: fDynRangeFactor, RearDynamicRangeFactor: rDynRangeFactor,
            DynamicRangeDifference: dynRangeDiff,
            WheelLoadSchemaVersion: CurrentBalanceMetricsSchemaVersion);
    }

    /// <summary>
    /// Counts unloading events with Schmitt-trigger hysteresis to suppress spurious
    /// re-triggers from spike noise around the threshold.
    ///
    /// State machine:
    ///   loaded  → unloading: force drops below <paramref name="enterThreshold"/>
    ///   unloading → loaded:  force rises above <paramref name="exitThreshold"/>
    /// An unloading run only counts as an event when it lasted at least
    /// <paramref name="minSamples"/> samples while in the "unloading" state.
    ///
    /// "% time" is the fraction of considered samples that were below the *enter*
    /// threshold (the strict "really unloaded" definition; the hysteresis exit only
    /// affects event counting, not the dwell percentage).
    ///
    /// Samples flagged in <paramref name="skipMask"/> (e.g. airtime where both wheels
    /// are trivially unloaded) are excluded from both numerator and denominator, and
    /// they close any active unloading run so airborne segments can't merge into one
    /// giant event.
    /// </summary>
    private static (double Pct, int Events) UnloadingStats(double[] force,
        double enterThreshold, double exitThreshold, int minSamples, bool[]? skipMask = null)
    {
        int below = 0, considered = 0, events = 0, runLen = 0;
        bool unloading = false;
        for (int i = 0; i < force.Length; i++)
        {
            if (skipMask != null && i < skipMask.Length && skipMask[i])
            {
                if (unloading && runLen >= minSamples) events++;
                unloading = false;
                runLen = 0;
                continue;
            }
            considered++;
            var f = force[i];

            if (unloading)
            {
                runLen++;
                if (f < enterThreshold) below++;
                if (f > exitThreshold)
                {
                    if (runLen >= minSamples) events++;
                    unloading = false;
                    runLen = 0;
                }
            }
            else
            {
                if (f < enterThreshold)
                {
                    unloading = true;
                    runLen = 1;
                    below++;
                }
            }
        }
        if (unloading && runLen >= minSamples) events++;
        var pct = considered == 0 ? 0.0 : 100.0 * below / considered;
        return (pct, events);
    }

    /// <summary>
    /// Motion-based airtime detection used to exclude airborne samples from the
    /// wheel-unloading metric. Independent of the strict top-out heuristic in Strokes —
    /// real air-shocks rarely reach travel ≤ 3 mm because of internal friction and the
    /// negative-spring equilibrium offset.
    ///
    /// A sample is flagged airborne when both axles are simultaneously "quiet & light":
    ///   |velocity| ≤ 150 mm/s   (spring effectively still)
    ///   travel ≤ 0.4 × median(travel)   (well below dynamic sag)
    /// for at least 200 ms continuously, AND there is a landing-velocity spike
    /// (≥ 500 mm/s on either axle) within 500 ms after the run.
    ///
    /// The travel threshold scales with the rider's setup via the session median,
    /// so it works for both stiff DH and softer trail setups without re-tuning.
    /// Returns null if motion data is missing.
    /// </summary>
    private static bool[]? BuildAirtimeMaskFromMotion(int length, int sampleRate, Suspension front, Suspension rear)
    {
        if (length <= 0 || sampleRate <= 0) return null;
        if (!front.Present || !rear.Present) return null;
        var ft = front.Travel; var fv = front.Velocity;
        var rt = rear.Travel;  var rv = rear.Velocity;
        if (ft == null || fv == null || rt == null || rv == null) return null;
        if (ft.Length < length || fv.Length < length || rt.Length < length || rv.Length < length) return null;

        const double VelQuietMmS    = 150.0;   // spring nearly still
        const double TravelRelative = 0.40;    // travel ≤ 40 % of dyn-sag = clearly unloaded
        const double MinDurationS   = 0.10;    // 100 ms minimum — catches small caps/hops too
        const double LandingVelMmS  = 500.0;   // landing-compression spike threshold
        const double LandingWindowS = 0.50;    // search window after run

        var fMedian = MedianCopy(ft, length);
        var rMedian = MedianCopy(rt, length);
        // Defensive lower bound: if median is tiny (low-sag setup), keep the threshold at
        // a sensible absolute floor so isolated sensor noise can't trigger detection.
        var fThr = Math.Max(2.0, fMedian * TravelRelative);
        var rThr = Math.Max(2.0, rMedian * TravelRelative);

        var quiet = new bool[length];
        for (int i = 0; i < length; i++)
        {
            bool fQuiet = ft[i] <= fThr && Math.Abs(fv[i]) <= VelQuietMmS;
            bool rQuiet = rt[i] <= rThr && Math.Abs(rv[i]) <= VelQuietMmS;
            quiet[i] = fQuiet && rQuiet;
        }

        int minSamples     = Math.Max(1, (int)(MinDurationS   * sampleRate));
        int landingSamples = Math.Max(1, (int)(LandingWindowS * sampleRate));

        var mask = new bool[length];
        int runStart = -1;
        for (int i = 0; i <= length; i++)
        {
            bool isQuiet = i < length && quiet[i];
            if (isQuiet)
            {
                if (runStart < 0) runStart = i;
                continue;
            }
            if (runStart >= 0)
            {
                int runLen = i - runStart;
                if (runLen >= minSamples)
                {
                    // Confirm landing within the search window — ensures the run is a real
                    // jump rather than e.g. the rider standing at the trailhead with light
                    // weight on the bike.
                    int searchEnd = Math.Min(length, i + landingSamples);
                    bool hasLanding = false;
                    for (int j = i; j < searchEnd; j++)
                    {
                        if (Math.Abs(fv[j]) >= LandingVelMmS || Math.Abs(rv[j]) >= LandingVelMmS)
                        {
                            hasLanding = true;
                            break;
                        }
                    }
                    if (hasLanding)
                    {
                        for (int j = runStart; j < i; j++) mask[j] = true;
                    }
                }
                runStart = -1;
            }
        }
        return mask;
    }

    /// <summary>Median over the first <paramref name="length"/> samples of <paramref name="data"/>.</summary>
    private static double MedianCopy(double[] data, int length)
    {
        if (length <= 0) return 0;
        var copy = new double[length];
        Array.Copy(data, copy, length);
        Array.Sort(copy);
        return length % 2 == 1
            ? copy[length / 2]
            : 0.5 * (copy[length / 2 - 1] + copy[length / 2]);
    }

    /// <summary>
    /// Dynamic range expressed as a ratio (max − min) / staticForce. Reads as a multiplier:
    /// 1.0 = swings within ±static load, 3.0 = three times the static load, etc. Easier to
    /// interpret than a percentage where typical trail values land around 300–500 %.
    /// </summary>
    private static double DynamicRangeFactor(double[] force, double staticForce)
    {
        if (force.Length == 0 || staticForce <= 0) return 0;
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        for (int i = 0; i < force.Length; i++)
        {
            if (force[i] < min) min = force[i];
            if (force[i] > max) max = force[i];
        }
        return (max - min) / staticForce;
    }

    /// <summary>
    /// Statically-normalised Michelson load index per frequency band:
    /// M = (σF/F_F_static − σR/F_R_static) / (σF/F_F_static + σR/F_R_static)
    /// where σ = √∫P(f) df over [fLow, fHigh].
    /// </summary>
    private static double? LoadMichelson(double[] freqs, double[] pxxF, double[] pxxR,
        double fStatic, double rStatic, double fLow, double fHigh)
    {
        var energyF = IntegrateBand(freqs, pxxF, fLow, fHigh);
        var energyR = IntegrateBand(freqs, pxxR, fLow, fHigh);
        if (energyF <= 0 && energyR <= 0) return null;
        var sigmaF = Math.Sqrt(Math.Max(0, energyF)) / fStatic;
        var sigmaR = Math.Sqrt(Math.Max(0, energyR)) / rStatic;
        var sum = sigmaF + sigmaR;
        if (sum <= 1e-12) return null;
        return (sigmaF - sigmaR) / sum;
    }

    #endregion
};
