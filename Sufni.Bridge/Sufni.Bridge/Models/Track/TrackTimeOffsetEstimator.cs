using System;
using System.Collections.Generic;

namespace Sufni.Bridge.Models;

public static class TrackTimeOffsetEstimator
{
    public const long SearchRangeMs = 120_000;   // +/- 2 min
    public const long StepMs = 1_000;
    public const double MinDropMeters = 10.0;    // a session must actually descend to count
    public const long MaxSpreadMs = 30_000;      // outlier rejection around the median
    public const double MaxPointGapSeconds = 3.0;// endpoint must be covered by real GPX points

    /// <summary>
    /// Returns the estimated offset in ms (gpxTime = sstTime + offset), or null when the data
    /// does not support an estimate (fewer than 3 usable sessions).
    /// </summary>
    public static long? Estimate(TrackPoints points, IReadOnlyList<WallClockSlice> sessionIntervals)
    {
        if (points.TimeMs.Length < 2 || points.Ele.Length != points.TimeMs.Length)
            return null;

        var times = points.TimeMs;
        var ele = points.Ele;
        var maxGapMs = (long)(MaxPointGapSeconds * 1000.0);
        var candidates = new List<long>();

        foreach (var interval in sessionIntervals)
        {
            if (interval.DurationMs <= 0)
                continue;

            var startMs = interval.StartUnixMs;
            var endMs = startMs + interval.DurationMs;
            var found = false;
            var bestLag = 0L;
            var bestScore = double.NegativeInfinity;

            for (var lag = -SearchRangeMs; lag <= SearchRangeMs; lag += StepMs)
            {
                if (!TryNearest(times, startMs + lag, maxGapMs, out var iStart))
                    continue;
                if (!TryNearest(times, endMs + lag, maxGapMs, out var iEnd))
                    continue;

                // Tie-break towards the smallest correction: repeated similar descents (lap after
                // lap on the same trail) score identically at several lags, and without evidence
                // the smallest clock correction is the better prior.
                var score = ele[iStart] - ele[iEnd];
                if (!found || score > bestScore ||
                    (score == bestScore && Math.Abs(lag) < Math.Abs(bestLag)))
                {
                    bestScore = score;
                    bestLag = lag;
                    found = true;
                }
            }

            if (found && bestScore >= MinDropMeters)
                candidates.Add(bestLag);
        }

        if (candidates.Count < 3)
            return null;

        var median = Median(candidates);
        var clustered = new List<long>(candidates.Count);
        foreach (var lag in candidates)
        {
            if (Math.Abs(lag - median) <= MaxSpreadMs)
                clustered.Add(lag);
        }

        if (clustered.Count == 0)
            clustered = candidates;

        median = Median(clustered);
        return (long)Math.Round(median / 1000.0) * 1000;
    }

    private static bool TryNearest(long[] times, long t, long maxGapMs, out int index)
    {
        index = -1;
        var found = Array.BinarySearch(times, t);
        int left;
        int right;
        if (found >= 0)
        {
            left = found;
            right = found;
        }
        else
        {
            var insert = ~found;
            left = insert - 1;
            right = insert;
        }

        var best = -1;
        var bestDist = long.MaxValue;
        if (left >= 0)
        {
            var dist = Math.Abs(times[left] - t);
            best = left;
            bestDist = dist;
        }

        if (right < times.Length)
        {
            var dist = Math.Abs(times[right] - t);
            if (dist < bestDist)
            {
                best = right;
                bestDist = dist;
            }
        }

        if (best < 0 || bestDist > maxGapMs)
            return false;

        index = best;
        return true;
    }

    private static long Median(List<long> values)
    {
        var sorted = values.ToArray();
        Array.Sort(sorted);
        var n = sorted.Length;
        if ((n & 1) == 1)
            return sorted[n / 2];
        return (sorted[n / 2 - 1] + sorted[n / 2]) / 2;
    }
}
