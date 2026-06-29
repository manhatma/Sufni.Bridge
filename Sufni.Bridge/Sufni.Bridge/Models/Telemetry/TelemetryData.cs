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

    // Raw shock/damper travel before the leverage polynomial. Only populated for the rear
    // suspension; null for front (where the head-angle factor is linear). Used to smooth
    // on the finer-quantised shock signal (~2.84 µm/LSB) instead of the polynomial-mapped
    // wheel travel (~7 µm/LSB). Older sessions deserialise it as null and Reprocess
    // reconstructs it via Linkage.WheelToDamperTravel.
    public double[]? ShockTravel { get; set; }
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
    double? HeadAngleStaticDeg,
    double? HeadAngleShiftDeg,
    // --- Time-domain pitch attitude (laufzeit-corrected front/rear vertical travel) ---
    double? PitchMeanDeg,            // μ — mean chassis pitch (nose-down positive)
    double? PitchStabilityDeg,       // σ — std-dev of the pitch attitude over time
    double? PitchModeEnergyFraction, // B-index — ∫Spp / ∫(Spp+Shh) over the body band
    double? GoutAsymmetryPct,        // % of paired G-out events with |front%−rear%| > 25 pp
    int? GoutEventCount,             // N paired G-out events — gates the asymmetry headline at small N
    double? MaxFrontTravelMm,        // geometry passthrough for the μ expected band
    double? MaxRearTravelMm,
    double? WheelbaseMm);

[MessagePackObject(keyAsPropertyName: true)]
public class TelemetryData
{
    public const int TravelBinsForVelocityHistogram = 10;

    // Increment when velocity processing parameters change (e.g. smoother lambda).
    // Blobs with a lower version are automatically re-processed from Travel arrays on load.
    public const int CurrentProcessingVersion = 21;

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

    /// <summary>
    /// Smooths the rear shock-travel signal with WH (where ADS1115 quantisation is ~2.84 µm/LSB
    /// for an ELPM75 versus ~7 µm/LSB after the leverage polynomial), then maps the smoothed shock
    /// signal through the polynomial to obtain wheel travel for differentiation. Compared to
    /// smoothing already-mapped wheel travel, this gives the WH filter ~2.5× finer input
    /// resolution, which shortens the LSB plateaus that cause the v≈0 horizontal artefacts.
    /// </summary>
    private double[] SmoothedRearWheelTravel(double[] shockTravel, WhittakerHendersonSmoother smoother)
    {
        var smoothedShock = smoother.Smooth(shockTravel);
        var n = smoothedShock.Length;
        var smoothedWheel = new double[n];
        var maxRear = Linkage.MaxRearTravel;
        for (var i = 0; i < n; i++)
        {
            var w = Linkage.Polynomial.Evaluate(smoothedShock[i]);
            if (w < 0) w = 0;
            if (w > maxRear) w = maxRear;
            smoothedWheel[i] = w;
        }
        return smoothedWheel;
    }

    /// <summary>
    /// Reconstructs rear shock travel from previously stored wheel travel via numerical
    /// inversion of the leverage polynomial. Used during Reprocess of older sessions
    /// that were imported before ShockTravel was persisted.
    /// </summary>
    private double[] ReconstructShockTravel(double[] wheelTravel)
    {
        var n = wheelTravel.Length;
        var shock = new double[n];
        for (var i = 0; i < n; i++)
        {
            shock[i] = Linkage.WheelToDamperTravel(wheelTravel[i]);
        }
        return shock;
    }

    private static double[] ComputeVelocity(double[] travel, int sampleRate)
    {
        var n = travel.Length;
        var v = new double[n];

        if (n == 0)
            return v;
        if (n == 1)
            return v;

        // Forward at start, 3-tap one sample in, 5-tap interior, 3-tap one before end, backward at end.
        // 5-tap central difference (4th-order accurate): v[i] = (-x[i-2] - 8 x[i-1] + 8 x[i+1] + x[i+2]) / (12 dt).
        // Wider aperture bridges the LSB plateaus that occur during slow motion (< ~6 mm/s),
        // where consecutive samples sit on the same ADC code and the 3-tap derivative would emit zeros.
        v[0] = (travel[1] - travel[0]) * sampleRate;
        if (n >= 3)
            v[1] = (travel[2] - travel[0]) * sampleRate / 2.0;

        for (var i = 2; i < n - 2; i++)
        {
            v[i] = (-travel[i - 2] - 8.0 * travel[i - 1] + 8.0 * travel[i + 1] + travel[i + 2]) * sampleRate / 12.0;
        }

        if (n >= 3)
            v[n - 2] = (travel[n - 1] - travel[n - 3]) * sampleRate / 2.0;
        v[n - 1] = (travel[n - 1] - travel[n - 2]) * sampleRate;

        RejectSingleSampleSpikes(v, sampleRate);
        return v;
    }

    // Reject isolated 1-sample velocity outliers. Taylor expansion of a smooth signal v(t):
    //   v[i] − ½(v[i-1]+v[i+1])  =  −½·dt²·v''(t) + O(dt⁴)
    // For the velocity signal v(t), v''(t) is d²v/dt² = jerk (NOT acceleration — that
    // would be the case for the position signal). So the deviation equals ½·dt²·|jerk|,
    // and exceeding ½·dt²·SpikeJerkLimit means the implied per-sample jerk is non-physical.
    // The local velocity gradient drops out of the Taylor expansion, so legitimate fast
    // transitions pass naturally without any heuristic span-tolerance term.
    private static void RejectSingleSampleSpikes(double[] v, int sampleRate)
    {
        if (v.Length < 3) return;
        var dt = 1.0 / sampleRate;
        var floor = 0.5 * Parameters.SpikeJerkLimit * dt * dt;
        var orig = (double[])v.Clone();
        for (var i = 1; i < v.Length - 1; i++)
        {
            var expected = 0.5 * (orig[i - 1] + orig[i + 1]);
            if (Math.Abs(orig[i] - expected) > floor)
                v[i] = expected;
        }
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
        var smoother = new WhittakerHendersonSmoother(Parameters.WhOrder, Parameters.WhLambda);

        if (Front.Present)
        {
            Front.Travel = new double[fc];
            var frontCoeff = Math.Sin(Linkage.HeadAngle * Math.PI / 180.0);

            var lastValidFront = 0.0;
            var sawValidFront = false;
            for (var i = 0; i < front.Length; i++)
            {
                // Front travel might under/overshoot because of erroneous data
                // acquisition. Errors might occur mid-ride (e.g. broken electrical
                // connection due to vibration), so we don't error out, just cap
                // travel. Errors like these will be obvious on the graphs, and
                // the affected regions can be filtered by hand.
                var travel = Front.Calibration!.Evaluate(front[i]);
                // A NaN (null delegate / failed evaluation) would propagate through the
                // Cholesky smoother and destroy the entire velocity array. Hold the last
                // valid sample instead of injecting NaN or an artificial zero-spike.
                if (double.IsNaN(travel))
                    travel = lastValidFront;
                else
                {
                    lastValidFront = travel;
                    sawValidFront = true;
                }
                var x = travel * frontCoeff;
                x = Math.Max(0, x);
                x = Math.Min(x, Linkage.MaxFrontTravel);
                Front.Travel[i] = x;
            }

            if (!sawValidFront)
                throw new Exception("Front calibration produced no valid samples!");

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
            Rear.ShockTravel = new double[rc];

            var lastValidRear = 0.0;
            var sawValidRear = false;
            for (var i = 0; i < rear.Length; i++)
            {
                // Rear travel might also overshoot the max because of
                //  a) inaccurately measured leverage ratio
                //  b) inaccuracies introduced by polynomial fitting
                // So we just cap it at calculated maximum.
                var shock = Rear.Calibration!.Evaluate(rear[i]);
                // Guard NaN before storing: ShockTravel feeds the Cholesky smoother, where
                // a single NaN would destroy the whole velocity array. Hold last valid.
                if (double.IsNaN(shock))
                    shock = lastValidRear;
                else
                {
                    lastValidRear = shock;
                    sawValidRear = true;
                }
                Rear.ShockTravel[i] = shock;
                var x = Linkage.Polynomial.Evaluate(shock);
                x = Math.Max(0, x);
                x = Math.Min(x, Linkage.MaxRearTravel);
                Rear.Travel[i] = x;
            }

            if (!sawValidRear)
                throw new Exception("Rear calibration produced no valid samples!");

            var tbins = Linspace(0, Linkage.MaxRearTravel, Parameters.TravelHistBins + 1);
            var dt = Digitize(Rear.Travel, tbins);
            Rear.TravelBins = tbins;

            var v = ComputeVelocity(SmoothedRearWheelTravel(Rear.ShockTravel, smoother), SampleRate);
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
        var smoother = new WhittakerHendersonSmoother(Parameters.WhOrder, Parameters.WhLambda);

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
            // Sessions imported before ShockTravel was persisted reconstruct it from the
            // stored wheel travel via numerical inversion of the polynomial. After Reprocess
            // the array is cached so subsequent loads skip the reconstruction cost.
            if (Rear.ShockTravel is null || Rear.ShockTravel.Length != Rear.Travel.Length)
                Rear.ShockTravel = ReconstructShockTravel(Rear.Travel);

            // Re-bake wheel travel from shock travel through the current shock→wheel
            // polynomial, so the corrected intercept-free fit propagates to existing
            // sessions (mirrors the bake in ProcessRecording).
            var maxRear = Linkage.MaxRearTravel;
            for (var i = 0; i < Rear.ShockTravel.Length; i++)
                Rear.Travel[i] = Math.Clamp(Linkage.Polynomial.Evaluate(Rear.ShockTravel[i]), 0, maxRear);

            var tbins = Linspace(0, Linkage.MaxRearTravel, Parameters.TravelHistBins + 1);
            var dt = Digitize(Rear.Travel, tbins);
            Rear.TravelBins = tbins;

            var v = ComputeVelocity(SmoothedRearWheelTravel(Rear.ShockTravel, smoother), SampleRate);
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
    private static double[] ConcatenateTravelWithTransitions(
        List<double[]> travelArrays, int sampleRate, out List<(int Start, int End)> segments)
    {
        // Transition duration: 0.5s — long enough that even a full-travel ramp
        // stays within normal velocity range (e.g. 200mm / 0.5s = 400 mm/s)
        var transitionSamples = sampleRate / 2;
        var result = new List<double>(travelArrays.Sum(a => a.Length) + transitionSamples * (travelArrays.Count - 1));
        segments = new List<(int Start, int End)>(travelArrays.Count);

        for (var s = 0; s < travelArrays.Count; s++)
        {
            if (s > 0)
            {
                var from = result[^1];
                var to = travelArrays[s][0];
                for (var i = 1; i <= transitionSamples; i++)
                    result.Add(from + (to - from) * i / transitionSamples);
            }

            // Record the [start, end) span of this real session's samples in the
            // combined array so stroke detection can skip the synthetic ramps.
            var start = result.Count;
            result.AddRange(travelArrays[s]);
            segments.Add((start, result.Count));
        }

        return result.ToArray();
    }

    /// <summary>
    /// Runs stroke detection per real-session segment (skipping the synthetic transition
    /// ramps inserted between sessions) and remaps the resulting stroke indices back to the
    /// combined array. Keeping strokes from ever spanning a ramp removes the ramp samples
    /// from every (stroke-based) statistic without touching the global time-series arrays.
    /// </summary>
    private static Stroke[] FilterStrokesSegmented(
        double[] velocity, double[] travel, double maxTravel, int sampleRate,
        List<(int Start, int End)> segments)
    {
        var strokes = new List<Stroke>();
        foreach (var (start, end) in segments)
        {
            if (end <= start) continue;

            var segStrokes = Strokes.FilterStrokes(
                velocity[start..end], travel[start..end], maxTravel, sampleRate);

            foreach (var st in segStrokes)
            {
                // Slice-local indices → global combined-array indices. Stat/Length/
                // Duration were already computed value-based from the slice, so only
                // Start/End need shifting; the later Digitize() then slices the global
                // digitized arrays with these global indices.
                st.Start += start;
                st.End += start;
                strokes.Add(st);
            }
        }

        return [.. strokes];
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

        var smoother = new WhittakerHendersonSmoother(Parameters.WhOrder, Parameters.WhLambda);

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

            // Slice (or reconstruct) ShockTravel to the same window so the smoother sees
            // the finer-quantised shock signal instead of polynomial-mapped wheel travel.
            if (Rear.ShockTravel is { Length: > 0 } && Rear.ShockTravel.Length == Rear.Travel.Length)
                cropped.Rear.ShockTravel = Rear.ShockTravel[s..e];
            else
                cropped.Rear.ShockTravel = ReconstructShockTravel(cropped.Rear.Travel);

            var tbins = Linspace(0, Linkage.MaxRearTravel, Parameters.TravelHistBins + 1);
            var dt = Digitize(cropped.Rear.Travel, tbins);
            cropped.Rear.TravelBins = tbins;

            var v = ComputeVelocity(SmoothedRearWheelTravel(cropped.Rear.ShockTravel, smoother), SampleRate);
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

        // Defensive: concatenation order must follow time, not caller order, to keep
        // the combined timeline and Timestamp = sessions.Min(...) consistent.
        sessions = sessions.OrderBy(s => s.Timestamp).ToList();

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

        var smoother = new WhittakerHendersonSmoother(Parameters.WhOrder, Parameters.WhLambda);

        if (hasFront)
        {
            combined.Front.Travel = ConcatenateTravelWithTransitions(
                sessions.Select(s => s.Front.Travel).ToList(), first.SampleRate, out var frontSegments);
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

            var strokes = FilterStrokesSegmented(v, combined.Front.Travel,
                first.Linkage.MaxFrontTravel, first.SampleRate, frontSegments);
            combined.Front.Strokes.Categorize(strokes);
            if (combined.Front.Strokes.Compressions.Length == 0 && combined.Front.Strokes.Rebounds.Length == 0)
                combined.Front.Present = false;
            else
                combined.Front.Strokes.Digitize(dt, dv, dvFine);
        }

        if (hasRear)
        {
            combined.Rear.Travel = ConcatenateTravelWithTransitions(
                sessions.Select(s => s.Rear.Travel).ToList(), first.SampleRate, out var rearSegments);
            combined.Rear.Calibration = first.Rear.Calibration;

            // Concatenate per-session ShockTravel using the same transition logic, falling back
            // to numerical inversion of the polynomial for any session that pre-dates ShockTravel
            // persistence.
            var shockArrays = sessions.Select(s =>
                s.Rear.ShockTravel is { Length: > 0 } && s.Rear.ShockTravel.Length == s.Rear.Travel.Length
                    ? s.Rear.ShockTravel
                    : first.ReconstructShockTravel(s.Rear.Travel)).ToList();
            combined.Rear.ShockTravel = ConcatenateTravelWithTransitions(shockArrays, first.SampleRate, out _);

            var tbins = Linspace(0, first.Linkage.MaxRearTravel, Parameters.TravelHistBins + 1);
            var dt = Digitize(combined.Rear.Travel, tbins);
            combined.Rear.TravelBins = tbins;

            // Re-derive velocity from combined travel to avoid discontinuities at session boundaries
            var v = ComputeVelocity(first.SmoothedRearWheelTravel(combined.Rear.ShockTravel, smoother), first.SampleRate);
            combined.Rear.Velocity = v;
            var (vbins, dv) = DigitizeVelocity(v, Parameters.VelocityHistStep);
            combined.Rear.VelocityBins = vbins;
            var (vbinsFine, dvFine) = DigitizeVelocity(v, Parameters.VelocityHistStepFine);
            combined.Rear.FineVelocityBins = vbinsFine;

            var strokes = FilterStrokesSegmented(v, combined.Rear.Travel,
                first.Linkage.MaxRearTravel, first.SampleRate, rearSegments);
            combined.Rear.Strokes.Categorize(strokes);
            if (combined.Rear.Strokes.Compressions.Length == 0 && combined.Rear.Strokes.Rebounds.Length == 0)
                combined.Rear.Present = false;
            else
                combined.Rear.Strokes.Digitize(dt, dv, dvFine);
        }

        combined.CalculateAirTimes();
        combined.ProcessingVersion = CurrentProcessingVersion;
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

        var srcBins = suspension.TravelBins.Length - 1;
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
                var vbin = Math.Clamp(s.DigitizedVelocity[i], 0, hist.Length - 1);
                var tbin = Math.Clamp(s.DigitizedTravel[i] * TravelBinsForVelocityHistogram / srcBins, 0, TravelBinsForVelocityHistogram - 1);
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

        var srcBins = suspension.TravelBins.Length - 1;
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
                var vbinFine = Math.Clamp(s.FineDigitizedVelocity[i], 0, hist.Length - 1);
                var midpoint = suspension.FineVelocityBins[vbinFine] + stepFine / 2.0;
                if (midpoint <= -(highSpeedThreshold + stepFine / 2.0) ||
                    midpoint >= (highSpeedThreshold + stepFine / 2.0))
                    continue;

                var tbin = Math.Clamp(s.DigitizedTravel[i] * TravelBinsForVelocityHistogram / srcBins, 0, TravelBinsForVelocityHistogram - 1);
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
        var std = Math.Sqrt(m2 / n);

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

        foreach (var stroke in suspension.Strokes.Compressions.Concat(suspension.Strokes.Rebounds))
        {
            sum += stroke.Stat.SumTravel;
            count += stroke.Stat.Count;
            if (stroke.Stat.MaxTravel > mx)
            {
                mx = stroke.Stat.MaxTravel;
            }
        }

        // Bottom-outs occur at the peak of a compression; counting them on the
        // following rebound as well would double the single event at the reversal point.
        var bo = suspension.Strokes.Compressions.Sum(s => s.Stat.Bottomouts);

        return new TravelStatistics(mx, sum / count, bo);
    }

    public DetailedTravelStatistics CalculateDetailedTravelStatistics(SuspensionType type)
    {
        var suspension = type == SuspensionType.Front ? Front : Rear;
        var travelValues = new List<double>();

        // Bottom-outs occur at the peak of a compression; counting them on the
        // following rebound as well would double the single event at the reversal point.
        var bottomouts = suspension.Strokes.Compressions.Sum(s => s.Stat.Bottomouts);

        foreach (var stroke in suspension.Strokes.Compressions.Concat(suspension.Strokes.Rebounds))
        {
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

    // Gap threshold (in samples) between two strokes for inserting a NaN break in
    // phase-portrait plots. Strokes closer than this stay visually connected so a
    // compression → rebound transition through v=0 is drawn as one continuous loop.
    // Above the threshold we assume an unrelated event (long idle, restart) and break.
    // 10 samples ≈ 12 ms at 860 SPS.
    private const int PhasePortraitStrokeGapThreshold = 10;

    public PositionVelocityData CalculatePositionVelocityData(SuspensionType type)
    {
        var suspension = type == SuspensionType.Front ? Front : Rear;
        var arrayLen = Math.Min(suspension.Travel.Length, suspension.Velocity.Length);
        var travel = new List<double>();
        var velocity = new List<double>();

        var allStrokes = suspension.Strokes.Compressions
            .Concat(suspension.Strokes.Rebounds)
            .OrderBy(s => s.Start)
            .ToList();

        var lastEnd = int.MinValue;
        foreach (var s in allStrokes)
        {
            if (s.End < s.Start || s.Start < 0 || s.End >= arrayLen) continue;
            if (lastEnd != int.MinValue && s.Start - lastEnd > PhasePortraitStrokeGapThreshold)
            {
                travel.Add(double.NaN);
                velocity.Add(double.NaN);
            }
            for (var i = s.Start; i <= s.End; i++)
            {
                travel.Add(suspension.Travel[i]);
                velocity.Add(suspension.Velocity[i]);
            }
            lastEnd = s.End;
        }

        return new PositionVelocityData(travel.ToArray(), velocity.ToArray());
    }

    public PositionVelocityData CalculateDamperPositionVelocityData()
    {
        var arrayLen = Math.Min(Rear.Travel.Length, Rear.Velocity.Length);
        if (Rear.ShockTravel is not null)
            arrayLen = Math.Min(arrayLen, Rear.ShockTravel.Length);
        var travel = new List<double>();
        var velocity = new List<double>();

        // Local leverage = dWheel/dShock — converts stored wheel velocity into the
        // shock/damper-domain velocity that matches the X axis.
        var dPolynomial = Linkage.Polynomial.Differentiate();

        var allStrokes = Rear.Strokes.Compressions
            .Concat(Rear.Strokes.Rebounds)
            .OrderBy(s => s.Start)
            .ToList();

        var lastEnd = int.MinValue;
        foreach (var s in allStrokes)
        {
            if (s.End < s.Start || s.Start < 0 || s.End >= arrayLen) continue;
            if (lastEnd != int.MinValue && s.Start - lastEnd > PhasePortraitStrokeGapThreshold)
            {
                travel.Add(double.NaN);
                velocity.Add(double.NaN);
            }
            for (var i = s.Start; i <= s.End; i++)
            {
                var shockPos = Rear.ShockTravel?[i] ?? Linkage.WheelToDamperTravel(Rear.Travel[i]);
                var leverage = dPolynomial.Evaluate(shockPos);
                travel.Add(shockPos);
                velocity.Add(leverage > 0 ? Rear.Velocity[i] / leverage : 0);
            }
            lastEnd = s.End;
        }

        return new PositionVelocityData(travel.ToArray(), velocity.ToArray());
    }

    /// <summary>
    /// Rear-only velocity histogram in the shock/damper domain: bins by shaft position and
    /// shaft velocity (mm/s) instead of wheel position/velocity. A wheel-velocity bin mixes
    /// different shaft speeds across the stroke under progressive/degressive kinematics, but
    /// the damper's LS/HS knee sits at a fixed shaft speed — so this is the view that matches
    /// the clicker. Mirrors CalculateVelocityHistogram's stacking with the robust tbin rescale.
    /// </summary>
    public StackedHistogramData CalculateDamperVelocityHistogram()
    {
        var arrayLen = Math.Min(Rear.Travel.Length, Rear.Velocity.Length);
        if (Rear.ShockTravel is not null)
            arrayLen = Math.Min(arrayLen, Rear.ShockTravel.Length);

        // Local leverage = dWheel/dShock — converts stored wheel velocity into shaft velocity.
        var dPolynomial = Linkage.Polynomial.Differentiate();

        var shockPos = new List<double>();
        var shockVel = new List<double>();
        foreach (var s in Rear.Strokes.Compressions.Concat(Rear.Strokes.Rebounds))
        {
            if (s.End < s.Start || s.Start < 0 || s.End >= arrayLen) continue;
            for (var i = s.Start; i <= s.End; i++)
            {
                var pos = Rear.ShockTravel?[i] ?? Linkage.WheelToDamperTravel(Rear.Travel[i]);
                var leverage = dPolynomial.Evaluate(pos);
                shockPos.Add(pos);
                shockVel.Add(leverage > 0 ? Rear.Velocity[i] / leverage : 0);
            }
        }

        if (shockPos.Count == 0)
            return new StackedHistogramData([], []);

        var maxShock = Linkage.MaxRearStroke ?? shockPos.Max();
        if (maxShock <= 0) maxShock = shockPos.Max();
        if (maxShock <= 0) maxShock = 1;

        var tbins = Linspace(0, maxShock, Parameters.TravelHistBins + 1);
        var digitizedTravel = Digitize(shockPos.ToArray(), tbins);
        var (vbins, digitizedVelocity) = DigitizeVelocity(shockVel.ToArray(), Parameters.DamperVelocityHistStep);

        var srcBins = tbins.Length - 1;
        var hist = new double[vbins.Length - 1][];
        for (var i = 0; i < hist.Length; i++)
            hist[i] = Generate.Zeros(TravelBinsForVelocityHistogram);

        var totalCount = shockPos.Count;
        for (var i = 0; i < totalCount; i++)
        {
            var vbin = Math.Clamp(digitizedVelocity[i], 0, hist.Length - 1);
            var tbin = Math.Clamp(digitizedTravel[i] * TravelBinsForVelocityHistogram / srcBins, 0, TravelBinsForVelocityHistogram - 1);
            hist[vbin][tbin] += 1;
        }

        foreach (var travelHist in hist)
            for (var j = 0; j < TravelBinsForVelocityHistogram; j++)
                travelHist[j] = travelHist[j] / totalCount * 100.0;

        return new StackedHistogramData(vbins.ToList(), [.. hist]);
    }

    /// <summary>
    /// Normal fit of the rear shaft (damper-domain) velocity distribution, overlaid on the
    /// shaft-velocity histogram. Mirrors CalculateNormalDistribution but over shaft velocities
    /// (wheel velocity / local leverage) and the damper bin step. Y values are in mm/s.
    /// </summary>
    public NormalDistributionData CalculateDamperNormalDistribution()
    {
        var arrayLen = System.Math.Min(Rear.Travel.Length, Rear.Velocity.Length);
        if (Rear.ShockTravel is not null)
            arrayLen = System.Math.Min(arrayLen, Rear.ShockTravel.Length);

        var dPolynomial = Linkage.Polynomial.Differentiate();

        // Welford's online algorithm over shaft velocities
        long n = 0;
        double mean = 0.0, m2 = 0.0;
        double min = double.MaxValue, max = double.MinValue;

        foreach (var s in Rear.Strokes.Compressions.Concat(Rear.Strokes.Rebounds))
        {
            if (s.End < s.Start || s.Start < 0 || s.End >= arrayLen) continue;
            for (var i = s.Start; i <= s.End; i++)
            {
                var pos = Rear.ShockTravel?[i] ?? Linkage.WheelToDamperTravel(Rear.Travel[i]);
                var leverage = dPolynomial.Evaluate(pos);
                var v = leverage > 0 ? Rear.Velocity[i] / leverage : 0;
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
        var std = System.Math.Sqrt(m2 / n);

        var range = max - min;
        var ny = new double[100];
        for (int i = 0; i < 100; i++)
            ny[i] = min + i * range / 99;

        var pdf = new List<double>(100);
        for (int i = 0; i < 100; i++)
            pdf.Add(Normal.PDF(mu, std, ny[i]) * Parameters.DamperVelocityHistStep * 100);

        return new NormalDistributionData([.. ny], pdf);
    }

    public PositionVelocityData CalculateForkPositionVelocityData()
    {
        var sinHeadAngle = Math.Sin(Linkage.HeadAngle * Math.PI / 180.0);
        var arrayLen = Math.Min(Front.Travel.Length, Front.Velocity.Length);
        var travel = new List<double>();
        var velocity = new List<double>();

        var allStrokes = Front.Strokes.Compressions
            .Concat(Front.Strokes.Rebounds)
            .OrderBy(s => s.Start)
            .ToList();

        var lastEnd = int.MinValue;
        foreach (var s in allStrokes)
        {
            if (s.End < s.Start || s.Start < 0 || s.End >= arrayLen) continue;
            if (lastEnd != int.MinValue && s.Start - lastEnd > PhasePortraitStrokeGapThreshold)
            {
                travel.Add(double.NaN);
                velocity.Add(double.NaN);
            }
            // Fork-domain velocity = wheel-domain / sin(HA), same constant scale as the
            // travel conversion. Stored Front.Travel is travel·sin(HA) and Front.Velocity
            // is its derivative, so both undo by the same factor.
            for (var i = s.Start; i <= s.End; i++)
            {
                travel.Add(sinHeadAngle > 0 ? Front.Travel[i] / sinHeadAngle : 0);
                velocity.Add(sinHeadAngle > 0 ? Front.Velocity[i] / sinHeadAngle : 0);
            }
            lastEnd = s.End;
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

        // Evaluate the front/rear fit difference only over the travel%-range actually
        // covered by BOTH suspensions, so the MSD is not dominated by extrapolation into
        // unoccupied travel ranges. TravelVelocity returns the travel% arrays sorted ascending.
        var frontT = frontTravelVelocity.Item1;
        var rearT = rearTravelVelocity.Item1;
        double msd;
        if (frontT.Length == 0 || rearT.Length == 0)
        {
            msd = 0;
        }
        else
        {
            var lo = Math.Max(frontT[0], rearT[0]);
            var hi = Math.Min(frontT[^1], rearT[^1]);
            if (lo >= hi)
            {
                msd = 0;
            }
            else
            {
                var evalPoints = Enumerable.Range(0, 100).Select(i => lo + (hi - lo) * (i + 0.5) / 100.0).ToArray();
                msd = evalPoints.Average(t => frontPoly(t) - rearPoly(t));
            }
        }

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
        Discipline.Trail    => 2.4, // new
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

    // ---------------------------------------------------------------------------------------
    // Time-domain pitch attitude
    //
    // The chassis pitch is the angle between the front- and rear-axle contact heights. A naive
    // instantaneous rear[i]−front[i] is contaminated by wheelbase-traversal delay: the fork hits
    // a bump τ seconds before the rear wheel does, so a single ground feature shows up as two
    // out-of-phase pseudo-pitch spikes (zeitversetztes Heave, not real chassis pitch). τ sits
    // inside the pitch band (~1.3–3.3 Hz) and there is no speed channel, so τ is estimated from
    // the signals themselves and the rear is pulled forward by τ before differencing.
    // ---------------------------------------------------------------------------------------

    [IgnoreMember]
    private (int lagSamples, double conf, bool determined)? frontRearLag;

    /// <summary>Front→rear traversal lag (rear lags front by this many samples) with a 0..1
    /// confidence and a determinability flag, estimated once and cached.</summary>
    private (int lagSamples, double conf, bool determined) GetFrontRearLag()
    {
        frontRearLag ??= EstimateFrontRearLag();
        return frontRearLag.Value;
    }

    /// <summary>
    /// Front→rear traversal lag τ in seconds — the time for the rear wheel to reach a terrain
    /// feature the front already passed. 0 when not determinable (see <see cref="LagDeterminable"/>).
    /// Public surface for plot annotation and the de-lag of pitch / G-out.
    /// </summary>
    [IgnoreMember]
    public double LagSeconds => SampleRate > 0 ? (double)GetFrontRearLag().lagSamples / SampleRate : 0.0;

    /// <summary>
    /// Whether τ could be pinned to a speed-plausible, coherent value. When false the lag is 0 and
    /// callers should treat it as "no de-lag" and label it "lag n/a" rather than showing a number.
    /// </summary>
    [IgnoreMember]
    public bool LagDeterminable => GetFrontRearLag().determined;

    /// <summary>
    /// Effective riding speed implied by the traversal lag (wheelbase / τ), in km/h, or null when
    /// the lag is not determinable. A sanity readout: realistic trail speeds are ~5–40 km/h.
    /// </summary>
    [IgnoreMember]
    public double? EffectiveSpeedKmh
    {
        get
        {
            var t = LagSeconds;
            if (!LagDeterminable || t <= 0 || Linkage is not { Wheelbase: > 0 }) return null;
            return Linkage.Wheelbase.Value / 1000.0 / t * 3.6;
        }
    }

    // Speed-plausible bounds for the traversal lag (m/s): τ = wheelbase / speed, so a 1.2 m
    // wheelbase implies τ ∈ [60, 600] ms. Anything outside means the estimate locked onto in-phase
    // heave (τ→0) or noise rather than the terrain traversal.
    private const double LagMinSpeedMs = 2.0;
    private const double LagMaxSpeedMs = 20.0;
    // Band isolating the terrain-following component for the cross-correlation: above the in-phase
    // body heave (τ≈0) and below the uncorrelated high-frequency wheel chatter.
    private const double LagBandLowHz = 3.0;
    private const double LagBandHighHz = 15.0;
    // Phase-slope fit band, anchored in the coherent body band [1, 6] Hz. The lower edge is kept at
    // 1 Hz on purpose: the in-phase heave that dominates there pins a heave-dominated trail's slope
    // to ~0/negative (⇒ "not determinable"), while a real traversal still shows a positive ramp. The
    // old [3, 10] Hz band drifted into the incoherent >6 Hz region, where unwrapping noise fabricated
    // a spurious positive slope (and an implausibly high speed). The coherence floor is the weight
    // cutoff below which a bin is wrapping noise.
    private const double LagPhaseLowHz = 1.0;
    private const double LagPhaseHighHz = 6.0;
    private const double LagCoherenceFloor = 0.3;
    private const double LagCcPeakMin = 0.3;          // min normalised CC for the correlation to confirm

    // Estimates the front→rear traversal lag τ via two independent methods over a speed-plausible
    // search window [wheelbase/vmax, wheelbase/vmin]:
    //   1. Band-passed (3–15 Hz, common-mode heave removed) normalised cross-correlation — its peak
    //      lag is the terrain traversal; the peak value is the confidence.
    //   2. Cross-spectrum phase slope dφ/df = 2π·τ over the coherent band, with the phase UNWRAPPED
    //      first (at these lags 2πfτ exceeds π within a few Hz, so the raw phase wraps).
    // Determinability is gated on the phase slope (positive, speed-plausible, coherent); the
    // correlation only confirms/refines it. Reports "not determinable" (τ=0) rather than inventing a
    // correction when the phase gives no usable estimate (e.g. heave-dominated trails).
    private (int lagSamples, double conf, bool determined) EstimateFrontRearLag()
    {
        if (!Front.Present || !Rear.Present ||
            Front.Travel is not { Length: > 0 } || Rear.Travel is not { Length: > 0 } ||
            SampleRate <= 0)
            return (0, 0, false);

        int n = Math.Min(Front.Travel.Length, Rear.Travel.Length);

        // Speed-plausible search window in samples. Without a wheelbase we cannot bound by speed,
        // so fall back to a broad 40–600 ms window and lean on coherence to stay honest.
        double wbM = Linkage is { Wheelbase: > 0 } ? Linkage.Wheelbase.Value / 1000.0 : 0.0;
        double tauMinSec = wbM > 0 ? wbM / LagMaxSpeedMs : 0.04;
        double tauMaxSec = wbM > 0 ? wbM / LagMinSpeedMs : 0.6;
        int lagMin = Math.Max(1, (int)Math.Round(tauMinSec * SampleRate));
        int lagMax = (int)Math.Round(tauMaxSec * SampleRate);
        if (lagMax > n / 2) lagMax = n / 2;
        if (lagMax < lagMin) return (0, 0, false);

        // --- Method 1: band-pass cross-correlation over the speed-plausible window ---------------
        var fbp = BandPassZeroPhase(Front.Travel[..n], SampleRate, LagBandLowHz, LagBandHighHz);
        var rbp = BandPassZeroPhase(Rear.Travel[..n], SampleRate, LagBandLowHz, LagBandHighHz);

        const int targetN = 100_000;
        int stride = n > targetN ? n / targetN : 1;
        double fNorm = 0, rNorm = 0;
        for (int i = 0; i < n; i += stride) { fNorm += fbp[i] * fbp[i]; rNorm += rbp[i] * rbp[i]; }
        double denom = Math.Sqrt(fNorm * rNorm);
        int ccLag = 0; double ccPeak = 0;
        if (denom > 1e-12)
        {
            for (int lag = lagMin; lag <= lagMax; lag++)
            {
                double s = 0;
                for (int i = 0; i + lag < n; i += stride) s += fbp[i] * rbp[i + lag];
                double cc = s / denom;
                if (cc > ccPeak) { ccPeak = cc; ccLag = lag; }
            }
        }
        double ccTauSec = (double)ccLag / SampleRate;

        // --- Method 2: unwrapped cross-spectrum phase slope dφ/df = 2π·τ -------------------------
        double phaseTauSec = double.NaN; int phaseBins = 0;
        var (cf, pxx, pyy, pxy) = ComputeWelchCrossSpectrum(Front.Travel[..n], Rear.Travel[..n], SampleRate);
        if (cf.Length > 2)
        {
            // Contiguous bins across the phase band so unwrap follows a real frequency sweep;
            // coherence becomes the fit weight (0 below the floor) rather than a hard gate.
            var pf = new List<double>(); var pp = new List<double>(); var pw = new List<double>();
            for (int k = 1; k < cf.Length; k++)
            {
                double freq = cf[k];
                if (freq < LagPhaseLowHz || freq > LagPhaseHighHz) continue;
                double d = pxx[k] * pyy[k];
                double g2 = d > 1e-30 ? (pxy[k].Real * pxy[k].Real + pxy[k].Imaginary * pxy[k].Imaginary) / d : 0.0;
                if (g2 > 1.0) g2 = 1.0;
                pf.Add(freq);
                // Pxy = X·conj(Y); rear (Y) lagging front (X) by τ ⇒ φ = +2π·f·τ.
                pp.Add(Math.Atan2(pxy[k].Imaginary, pxy[k].Real));
                pw.Add(g2 >= LagCoherenceFloor ? g2 : 0.0);
            }
            if (pf.Count >= 4)
            {
                var unwrapped = Unwrap(pp);
                double sw = 0, swf = 0, swp = 0, swff = 0, swfp = 0;
                for (int i = 0; i < pf.Count; i++)
                {
                    double w = pw[i];
                    if (w <= 0) continue;
                    phaseBins++;
                    sw += w; swf += w * pf[i]; swp += w * unwrapped[i];
                    swff += w * pf[i] * pf[i]; swfp += w * pf[i] * unwrapped[i];
                }
                double dnm = sw * swff - swf * swf;
                if (sw > 0 && Math.Abs(dnm) > 1e-12)
                {
                    double slope = (sw * swfp - swf * swp) / dnm;   // rad / Hz
                    phaseTauSec = slope / (2.0 * Math.PI);
                }
            }
        }

        // --- Decision: the phase slope is primary; the correlation only confirms ----------------
        // A real front→rear traversal shows a positive, speed-plausible phase slope in the coherent
        // body band. That is the only physically meaningful evidence, so it gates determinability.
        // The band-pass correlation merely *confirms* (blends when it agrees) — it is never trusted
        // on its own, because on heave-dominated trails it locks onto incidental short-lag
        // correlation and would fabricate an implausibly high speed. No usable phase ⇒ "lag n/a".
        bool phaseOk = !double.IsNaN(phaseTauSec) && phaseTauSec >= tauMinSec && phaseTauSec <= tauMaxSec
                       && phaseBins >= 4;
        if (!phaseOk) return (0, ccPeak, false);

        bool ccConfirms = ccPeak >= LagCcPeakMin && ccLag >= lagMin && ccLag <= lagMax
                          && Math.Abs(phaseTauSec - ccTauSec) <= Math.Max(0.04, 0.3 * phaseTauSec);
        double chosenSec = ccConfirms ? 0.5 * (phaseTauSec + ccTauSec) : phaseTauSec;

        int lagSamples = (int)Math.Round(chosenSec * SampleRate);
        if (lagSamples < 1) return (0, ccPeak, false);
        return (lagSamples, ccPeak, true);
    }

    // Zero-phase band-pass: difference of two zero-phase low-passes, keeping [fLow, fHigh] with no
    // group delay (so it cannot bias the lag estimate).
    private static double[] BandPassZeroPhase(double[] x, int sampleRate, double fLow, double fHigh)
    {
        var lo = LowPassZeroPhase(x, sampleRate, fLow);
        var hi = LowPassZeroPhase(x, sampleRate, fHigh);
        var y = new double[x.Length];
        for (int i = 0; i < x.Length; i++) y[i] = hi[i] - lo[i];
        return y;
    }

    // Unwraps a phase sequence so consecutive steps stay within (−π, π].
    private static double[] Unwrap(IReadOnlyList<double> phase)
    {
        var u = new double[phase.Count];
        if (phase.Count == 0) return u;
        u[0] = phase[0];
        for (int i = 1; i < phase.Count; i++)
        {
            double d = phase[i] - phase[i - 1];
            while (d > Math.PI) d -= 2.0 * Math.PI;
            while (d < -Math.PI) d += 2.0 * Math.PI;
            u[i] = u[i - 1] + d;
        }
        return u;
    }

    /// <summary>
    /// Chassis pitch attitude over time in degrees (nose-down positive, consistent with
    /// <c>HeadAngleShiftDeg</c>). The rear travel is pulled forward by the estimated
    /// front→rear lag, both signals are WH-smoothed, then
    /// <c>pitch(i) = −atan2(rear[i+τ] − front[i], wheelbase)</c>. Both travel arrays are vertical
    /// wheel travel (mm). Returns null without both suspensions or a wheelbase.
    /// </summary>
    public double[]? CalculatePitchDegrees()
    {
        if (!Front.Present || !Rear.Present) return null;
        if (Front.Travel is not { Length: > 0 } || Rear.Travel is not { Length: > 0 }) return null;
        if (Linkage is not { Wheelbase: > 0 }) return null;
        double wb = Linkage.Wheelbase.Value;

        int n = Math.Min(Front.Travel.Length, Rear.Travel.Length);
        var (lag, _, _) = GetFrontRearLag();

        var smoother = new WhittakerHendersonSmoother(Parameters.WhOrder, Parameters.WhLambda);
        var f = smoother.Smooth(Front.Travel[..n]);
        var r = smoother.Smooth(Rear.Travel[..n]);

        var pitch = new double[n];
        for (int i = 0; i < n; i++)
        {
            int ri = i + lag;
            if (ri >= n) ri = n - 1;
            double dz = r[ri] - f[i];                    // vertical rear − front (mm)
            pitch[i] = -Math.Atan2(dz, wb) * 180.0 / Math.PI;
        }

        // Band-limit to the rigid-body regime. The sprung mass can only pitch at low frequency
        // (around the body resonance); the full-bandwidth rear−front difference is dominated by
        // asynchronous wheel impacts (10–25 Hz), which are NOT chassis attitude. Low-passing here
        // makes σ a genuine "attitude constancy" measure and the plot read as attitude rather than
        // differential noise. The mean (μ) is preserved by the zero-phase average.
        return LowPassZeroPhase(pitch, SampleRate, PitchLowPassHz);
    }

    // Rigid-body pitch low-pass cutoff (−3 dB), in Hz. Keeps the body resonance, strongly
    // attenuates the wheel band (10–25 Hz) and above.
    private const double PitchLowPassHz = 4.0;

    // Zero-phase low-pass: three cascaded centered moving averages (≈ Gaussian kernel, low
    // sidelobes). The window radius is derived from the sample rate, so the cutoff stays in Hz
    // regardless of device rate. Edges shrink the window to the available samples (no padding
    // artefacts, no Gibbs ringing).
    private static double[] LowPassZeroPhase(double[] x, int sampleRate, double cutoffHz)
    {
        if (x.Length < 3 || sampleRate <= 0 || cutoffHz <= 0) return (double[])x.Clone();
        int radius = (int)Math.Round(0.13 * sampleRate / cutoffHz);
        if (radius < 1) return (double[])x.Clone();
        var y = MovingAverageCentered(x, radius);
        y = MovingAverageCentered(y, radius);
        y = MovingAverageCentered(y, radius);
        return y;
    }

    private static double[] MovingAverageCentered(double[] x, int radius)
    {
        int n = x.Length;
        var prefix = new double[n + 1];
        for (int i = 0; i < n; i++) prefix[i + 1] = prefix[i] + x[i];
        var y = new double[n];
        for (int i = 0; i < n; i++)
        {
            int a = Math.Max(0, i - radius);
            int b = Math.Min(n - 1, i + radius);
            y[i] = (prefix[b + 1] - prefix[a]) / (b - a + 1);
        }
        return y;
    }

    /// <summary>
    /// Modal decomposition of the front/rear travel cross-spectrum: pitch (anti-phase) and heave
    /// (in-phase) power spectra, plus magnitude-squared coherence γ²(f) and cross phase φ(f) in
    /// degrees. Spp = (Pxx+Pyy−2·Re Pxy)/4, Shh = (Pxx+Pyy+2·Re Pxy)/4. Null without enough data.
    /// </summary>
    public (double[] Freqs, double[] Coherence, double[] PhaseDeg, double[] PitchPsd, double[] HeavePsd)?
        CalculateModalSpectrum()
    {
        if (!Front.Present || !Rear.Present) return null;
        if (Front.Travel is not { Length: >= 8192 } || Rear.Travel is not { Length: >= 8192 }) return null;

        int n = Math.Min(Front.Travel.Length, Rear.Travel.Length);
        var (cf, pxx, pyy, pxy) = ComputeWelchCrossSpectrum(Front.Travel[..n], Rear.Travel[..n], SampleRate);
        if (cf.Length == 0) return null;

        int m = cf.Length;
        var coh = new double[m];
        var phase = new double[m];
        var spp = new double[m];
        var shh = new double[m];
        for (int k = 0; k < m; k++)
        {
            double re = pxy[k].Real, im = pxy[k].Imaginary;
            double d = pxx[k] * pyy[k];
            double g2 = d > 1e-30 ? (re * re + im * im) / d : 0.0;
            coh[k] = g2 > 1.0 ? 1.0 : g2;
            phase[k] = Math.Atan2(im, re) * 180.0 / Math.PI;
            spp[k] = Math.Max(0.0, (pxx[k] + pyy[k] - 2.0 * re) / 4.0);
            shh[k] = Math.Max(0.0, (pxx[k] + pyy[k] + 2.0 * re) / 4.0);
        }
        return (cf, coh, phase, spp, shh);
    }

    // G-out gate: a stroke drives an event when it is both deep and fast. Looser than the original
    // 0.55/0.5 so rear-led and moderate events are caught too (more events → more robust stats).
    private const double GoutDepthFrac = 0.45;
    private const double GoutVelFrac = 0.40;
    // Below this many events the asymmetry headline is small-N noise and is shown as indicative only.
    public const int GoutMinReliableEvents = 12;

    /// <summary>
    /// G-out load-symmetry events. Each deep+fast compression (front OR rear) is one event; the
    /// opposite end's response is read as the peak travel actually reached in the lag-aligned window
    /// — not the nearest stroke — so a genuinely uncompressed end reads ~0 % instead of snapping to a
    /// random micro-stroke. Front- and rear-led triggers for the same impact are de-duplicated.
    /// Returns each side's used travel (% of available). Null without both suspensions / geometry /
    /// strokes.
    /// </summary>
    public IReadOnlyList<(double FrontPct, double RearPct)>? CalculateGoutEvents()
    {
        if (!Front.Present || !Rear.Present) return null;
        if (Front.Travel is not { Length: > 0 } || Rear.Travel is not { Length: > 0 }) return null;
        var fStrokes = Front.Strokes?.Compressions;
        var rStrokes = Rear.Strokes?.Compressions;
        if (fStrokes is not { Length: > 0 } && rStrokes is not { Length: > 0 }) return null;
        double maxF = Linkage?.MaxFrontTravel ?? 0;
        double maxR = Linkage?.MaxRearTravel ?? 0;
        if (maxF <= 0 || maxR <= 0) return null;

        int n = Math.Min(Front.Travel.Length, Rear.Travel.Length);
        var (lag, _, _) = GetFrontRearLag();
        int tol = (int)Math.Round(0.2 * SampleRate) + Math.Abs(lag);   // response-window slack

        double fVelMax = fStrokes is { Length: > 0 } ? fStrokes.Max(s => s.Stat.MaxVelocity) : 0;
        double rVelMax = rStrokes is { Length: > 0 } ? rStrokes.Max(s => s.Stat.MaxVelocity) : 0;

        // Peak travel (mm) actually reached within [a, b], clamped to the data.
        double WindowMax(double[] travel, int a, int b)
        {
            if (a < 0) a = 0;
            if (b > n - 1) b = n - 1;
            double mx = 0;
            for (int i = a; i <= b; i++) if (travel[i] > mx) mx = travel[i];
            return mx;
        }

        // Each candidate tagged with the front-impact sample index, for dedup across the two triggers.
        var raw = new List<(int frontIdx, double frontPct, double rearPct)>();

        // Front-led: the front drives, the rear responds τ later in [Start+lag, End+lag+tol].
        if (fStrokes is { Length: > 0 } && fVelMax > 0)
            foreach (var fs in fStrokes)
            {
                if (fs.Stat.MaxTravel < GoutDepthFrac * maxF) continue;
                if (fs.Stat.MaxVelocity < GoutVelFrac * fVelMax) continue;
                var rearMm = WindowMax(Rear.Travel, fs.Start + lag, fs.End + lag + tol);
                raw.Add((fs.Start,
                    Math.Min(fs.Stat.MaxTravel / maxF * 100.0, 100.0),
                    Math.Min(rearMm / maxR * 100.0, 100.0)));
            }

        // Rear-led: the rear drives, the front impact was τ earlier in [Start−lag−tol, End−lag].
        if (rStrokes is { Length: > 0 } && rVelMax > 0)
            foreach (var rs in rStrokes)
            {
                if (rs.Stat.MaxTravel < GoutDepthFrac * maxR) continue;
                if (rs.Stat.MaxVelocity < GoutVelFrac * rVelMax) continue;
                var frontMm = WindowMax(Front.Travel, rs.Start - lag - tol, rs.End - lag);
                raw.Add((rs.Start - lag,
                    Math.Min(frontMm / maxF * 100.0, 100.0),
                    Math.Min(rs.Stat.MaxTravel / maxR * 100.0, 100.0)));
            }

        if (raw.Count == 0) return new List<(double FrontPct, double RearPct)>();

        // Dedup: front- and rear-led triggers for one impact land at ~the same front index. Sort by
        // impact, collapse neighbours within tol, keeping the heavier-loaded of the pair.
        raw.Sort((x, y) => x.frontIdx.CompareTo(y.frontIdx));
        var events = new List<(double FrontPct, double RearPct)>();
        int lastIdx = int.MinValue;
        foreach (var e in raw)
        {
            if (events.Count > 0 && e.frontIdx - lastIdx <= tol)
            {
                var prev = events[^1];
                if (e.frontPct + e.rearPct > prev.FrontPct + prev.RearPct)
                    events[^1] = (e.frontPct, e.rearPct);
            }
            else
            {
                events.Add((e.frontPct, e.rearPct));
            }
            lastIdx = e.frontIdx;
        }
        return events;
    }

    public BalanceMetrics CalculateBalanceMetrics(Discipline? discipline = null)
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
        double? haStaticDeg = null, haShiftDeg = null;
        double fSplit = FrequencySplitFor(discipline);

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

        // Effective head angle under mean dynamic sag. Pitch (nose-down positive)
        // tips the front of the chassis down → head angle becomes steeper, so the
        // signed shift is −φ in degrees.
        if (Linkage is { Wheelbase: > 0 } && Linkage.MaxFrontStroke is > 0
            && maxR > 0 && fSag.HasValue && rSag.HasValue)
        {
            var haRad = Linkage.HeadAngle * Math.PI / 180.0;
            var sf = fSag.Value / 100.0 * Linkage.MaxFrontStroke.Value;
            var sr = rSag.Value / 100.0 * maxR;
            var phi = Math.Atan2(sr - sf * Math.Sin(haRad), Linkage.Wheelbase.Value);
            haStaticDeg = Linkage.HeadAngle;
            haShiftDeg = -phi * 180.0 / Math.PI;
        }

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

        // Time-domain pitch attitude (laufzeit-corrected) + modal energy split + G-out symmetry.
        double? pitchMeanDeg = null, pitchStabilityDeg = null, pitchModeEnergy = null, goutAsymPct = null;
        int? goutEventCount = null;
        double? maxFrontTravelMm = maxF > 0 ? maxF : null;
        double? maxRearTravelMm = maxR > 0 ? maxR : null;
        double? wheelbaseMm = Linkage is { Wheelbase: > 0 } ? Linkage.Wheelbase : null;

        if (Front.Present && Rear.Present)
        {
            var pitch = CalculatePitchDegrees();
            if (pitch is { Length: > 0 })
            {
                double mean = 0;
                for (int i = 0; i < pitch.Length; i++) mean += pitch[i];
                mean /= pitch.Length;
                double variance = 0;
                for (int i = 0; i < pitch.Length; i++) { var d = pitch[i] - mean; variance += d * d; }
                variance /= pitch.Length;
                pitchMeanDeg = mean;
                pitchStabilityDeg = Math.Sqrt(variance);
            }

            var modal = CalculateModalSpectrum();
            if (modal is { } md)
            {
                var sum = new double[md.PitchPsd.Length];
                for (int k = 0; k < sum.Length; k++) sum[k] = md.PitchPsd[k] + md.HeavePsd[k];
                var ep = IntegrateBand(md.Freqs, md.PitchPsd, 1.0, fSplit);
                var et = IntegrateBand(md.Freqs, sum, 1.0, fSplit);
                if (et > 1e-30) pitchModeEnergy = ep / et;
            }

            var gout = CalculateGoutEvents();
            goutEventCount = gout?.Count;
            if (gout is { Count: > 0 })
            {
                int asym = 0;
                foreach (var (fp, rp) in gout) if (Math.Abs(fp - rp) > 25.0) asym++;
                goutAsymPct = 100.0 * asym / gout.Count;
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
            haStaticDeg, haShiftDeg,
            pitchMeanDeg, pitchStabilityDeg, pitchModeEnergy, goutAsymPct, goutEventCount,
            maxFrontTravelMm, maxRearTravelMm, wheelbaseMm);
    }

    #endregion
};
