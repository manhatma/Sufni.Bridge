using System;
using System.Collections.Generic;
using System.Linq;
using MathNet.Numerics;
using MathNet.Numerics.Distributions;
using MathNet.Numerics.Statistics;
using MessagePack;
using Generate = ScottPlot.Generate;

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

[MessagePackObject(keyAsPropertyName: true)]
public class TelemetryData
{
    public const int TravelBinsForVelocityHistogram = 10;

    // Increment when velocity processing parameters change (e.g. smoother lambda).
    // Blobs with a lower version are automatically re-processed from Travel arrays on load.
    public const int CurrentProcessingVersion = 3;

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

            var v = smoother.Smooth(ComputeVelocity(Front.Travel, SampleRate));
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

            var v = smoother.Smooth(ComputeVelocity(Rear.Travel, SampleRate));
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

            var v = smoother.Smooth(ComputeVelocity(Front.Travel, SampleRate));
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

            var v = smoother.Smooth(ComputeVelocity(Rear.Travel, SampleRate));
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

            var v = smoother.Smooth(ComputeVelocity(cropped.Front.Travel, SampleRate));
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

            var v = smoother.Smooth(ComputeVelocity(cropped.Rear.Travel, SampleRate));
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
            var v = smoother.Smooth(ComputeVelocity(combined.Front.Travel, first.SampleRate));
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
            var v = smoother.Smooth(ComputeVelocity(combined.Rear.Travel, first.SampleRate));
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
};
