using System;
using System.Collections.Generic;
using System.Linq;
using MessagePack;

#pragma warning disable CS8618

namespace Sufni.Bridge.Models.Telemetry;

[MessagePackObject(keyAsPropertyName: true)]
public class StrokeStat
{
    public double SumTravel { get; set; }
    public double MaxTravel { get; set; }
    public double SumVelocity { get; set; }
    public double MaxVelocity { get; set; }
    public int Bottomouts { get; set; }
    public int Count { get; set; }
};

[MessagePackObject(keyAsPropertyName: true)]
public class Stroke
{
    public int Start { get; set; }
    public int End { get; set; }
    public StrokeStat Stat { get; set; }
    public int[] DigitizedTravel { get; set; }
    public int[] DigitizedVelocity { get; set; }
    public int[] FineDigitizedVelocity { get; set; }

    [IgnoreMember] public double Length { get; private set; }
    [IgnoreMember] public double Duration { get; set; }
    [IgnoreMember] public bool AirCandidate { get; set; }

    public Stroke() { }

    public Stroke(int start, int end, double duration, double[] travel, double[] velocity, double maxTravel)
    {
        Start = start;
        End = end;
        Length = travel[end] - travel[start];
        Duration = duration;

        var mv = Length < 0 ? velocity[start..(end + 1)].Min() : velocity[start..(end + 1)].Max();
        var bo = 0;
        // Scan inclusive of the reversal sample (end): a compression's peak — the
        // bottom-most point where a bottom-out actually occurs — is the end sample.
        // Excluding it would drop "peak-only" bottom-outs (threshold first crossed at
        // the reversal point). The inner skip loop stays bounded by travel.Length.
        for (var i = start; i <= end; i++)
        {
            if (!(travel[i] > maxTravel - Parameters.BottomoutThreshold)) continue;
            bo += 1;
            for (; i < travel.Length && travel[i] > maxTravel - Parameters.BottomoutThreshold; i++) { }
        }

        Stat = new StrokeStat
        {
            SumTravel = travel[start..(end + 1)].Sum(),
            MaxTravel = travel[start..(end + 1)].Max(),
            SumVelocity = velocity[start..(end + 1)].Sum(),
            MaxVelocity = mv,
            Bottomouts = bo,
            Count = end - start + 1,
        };
    }

    /// <summary>
    /// Whether two strokes describe the same event. Measured against the SHORTER of the two:
    /// the front and rear hover strokes of one jump routinely differ in length by 2-3x (the
    /// fork snaps to top-out, the shock creeps there), and demanding that the short one cover
    /// half of the long one would leave both unmatched — each then reaching the single-sided
    /// fallback and reporting the jump twice.
    /// </summary>
    public bool Overlaps(Stroke other)
    {
        var l = Math.Min(End - Start, other.End - other.Start);
        var s = Math.Max(Start, other.Start);
        var e = Math.Min(End, other.End);
        return e - s >= Parameters.AirtimeOverlapThreshold * l;
    }
};

[MessagePackObject(keyAsPropertyName: true)]
public class Strokes
{
    public Stroke[] Compressions { get; set; }
    public Stroke[] Rebounds { get; set; }
    [IgnoreMember] public Stroke[] Idlings { get; private set; }

    /// <summary>
    /// Strokes during which the suspension plausibly hung unloaded near its top-out position.
    /// Deliberately a separate list from <see cref="Idlings"/>: an airborne element is only
    /// quasi-static (it keeps creeping out), so this test is looser than the idling test and
    /// admits strokes that are also booked as compressions or rebounds. Whether a candidate
    /// really is an airtime is decided in TelemetryData.CalculateAirTimes(), which cross-checks
    /// the front and rear candidates against each other.
    /// </summary>
    [IgnoreMember] public Stroke[] AirCandidates { get; private set; }

    /// <summary>
    /// The travel this element reads when fully extended. See Parameters.TopOutQuantile.
    /// </summary>
    [IgnoreMember] public double TopOut { get; private set; }

    /// <summary>
    /// Estimates the fully-extended ("topped out") travel reading of a suspension element as a
    /// low quantile of its travel distribution, so that airtime detection can be expressed
    /// relative to the element's own extension floor rather than to an absolute 0 mm that
    /// calibration offsets and top-out bumpers make unreachable.
    /// </summary>
    public static double EstimateTopOut(double[] travel, double maxTravel)
    {
        if (travel.Length == 0) return 0;

        var sorted = travel.Where(t => !double.IsNaN(t)).ToArray();
        if (sorted.Length == 0) return 0;
        Array.Sort(sorted);

        var index = (int)Math.Clamp(
            Parameters.TopOutQuantile * (sorted.Length - 1), 0, sorted.Length - 1);
        return Math.Clamp(sorted[index], 0, maxTravel * Parameters.TopOutMaxRatio);
    }

    public void Categorize(Stroke[] strokes, double[] travel, double maxTravel)
    {
        var compressions = new List<Stroke>();
        var rebounds = new List<Stroke>();
        var idlings = new List<Stroke>();
        var airCandidates = new List<Stroke>();

        TopOut = EstimateTopOut(travel, maxTravel);

        // Relative to the spring element's own travel, so short- and long-travel setups get
        // comparable sensitivity; the fixed AirtimeTravelThreshold remains a floor for very
        // short-travel setups (see its doc comment in Parameters.cs). Same ratio as the
        // settled check in RestsAtTopOut: stiction leaves an airborne element resting
        // anywhere within that band, and a candidate gate tighter than the settled gate
        // rejected real jumps whose fork stuck ~8 mm above an otherwise-reachable 0 mm.
        var airtimeTravelThreshold = TopOut + Math.Max(
            Parameters.AirtimeTravelThreshold, Parameters.AirtimeSettledTravelRatio * maxTravel);

        for (var i = 0; i < strokes.Length; i++)
        {
            var stroke = strokes[i];

            // A stroke is a possible airtime if the element hovered near its top-out position
            // (mean travel, so the initial extension ramp doesn't disqualify it), barely moved
            // over the stroke's duration, and was slammed by a landing right afterwards.
            // Independent of the compression/rebound/idling split below, because a slowly
            // extending shock is booked as a rebound yet is very much airborne.
            if (i > 0 && i < strokes.Length - 1 &&
                stroke.Duration >= Parameters.AirtimeDurationThreshold &&
                stroke.Duration <= Parameters.AirtimeDurationMax &&
                Math.Abs(stroke.Length) <=
                    Parameters.StrokeLengthThreshold + Parameters.AirtimeCreepRate * stroke.Duration &&
                stroke.Stat.SumTravel / stroke.Stat.Count <= airtimeTravelThreshold &&
                strokes[i + 1].Stat.MaxVelocity >= Parameters.AirtimeVelocityThreshold)
            {
                stroke.AirCandidate = true;
                airCandidates.Add(stroke);
            }

            if (Math.Abs(stroke.Length) < Parameters.StrokeLengthThreshold &&
                stroke.Duration >= Parameters.IdlingDurationThreshold)
            {
                idlings.Add(stroke);
            }
            else if (stroke.Length >= Parameters.StrokeLengthThreshold)
            {
                compressions.Add(stroke);
            }
            else if (stroke.Length <= -Parameters.StrokeLengthThreshold)
            {
                rebounds.Add(stroke);
            }
        }

        Compressions = [.. compressions];
        Rebounds = [.. rebounds];
        Idlings = [.. idlings];
        AirCandidates = [.. airCandidates];
    }

    public void Digitize(int[] dt, int[] dv, int[] dvFine)
    {
        foreach (var s in Compressions)
        {
            s.DigitizedTravel = dt[s.Start..(s.End + 1)];
            s.DigitizedVelocity = dv[s.Start..(s.End + 1)];
            s.FineDigitizedVelocity = dvFine[s.Start..(s.End + 1)];
        }

        foreach (var s in Rebounds)
        {
            s.DigitizedTravel = dt[s.Start..(s.End + 1)];
            s.DigitizedVelocity = dv[s.Start..(s.End + 1)];
            s.FineDigitizedVelocity = dvFine[s.Start..(s.End + 1)];
        }
    }

    public static Stroke[] FilterStrokes(double[] velocity, double[] travel, double maxTravel, int sampleRate)
    {
        var strokes = new List<Stroke>();
        int velocityLength = velocity.Length;

        for (int i = 0; i < velocityLength - 1; i++)
        {
            int startIndex = i;
            int startSign = Math.Sign(velocity[i]);
            double maxPosition = travel[startIndex];

            // Loop until velocity changes sign
            while (i < velocityLength - 1 && Math.Sign(velocity[i + 1]) == startSign)
            {
                i++;
                if (travel[i] > maxPosition)
                {
                    maxPosition = travel[i];
                }
            }

            // We are at the end of the data stream
            if (i >= velocityLength)
            {
                i = velocityLength - 1;
            }

            // Top-out periods often oscillate a bit, so they are split into multiple
            // strokes. We fix this by concatenating consecutive strokes if their
            // mean position is close to zero.
            double duration = (i - startIndex + 1) / (double)sampleRate;
            if (maxPosition < Parameters.StrokeLengthThreshold * Parameters.StrokeLengthThresholdFac &&
                strokes.Count > 0 &&
                strokes[^1].Stat.MaxTravel < Parameters.StrokeLengthThreshold * Parameters.StrokeLengthThresholdFac)
            {
                var prev = strokes[^1];
                strokes[^1] = new Stroke(prev.Start, i, prev.Duration + duration, travel, velocity, maxTravel);
            }
            else
            {
                strokes.Add(new Stroke(startIndex, i, duration, travel, velocity, maxTravel));
            }
        }

        return [.. strokes];
    }
};
