using Sufni.Bridge.Models;

namespace Sufni.Bridge.Tests;

public class TrackTimeOffsetEstimatorTests
{
    private const long BaseMs = 1_700_000_000_000;
    private const long KnownOffsetMs = 30_000;
    private const int DurationSeconds = 500;
    private const int DescentDurationSeconds = 20;
    private const double DropMeters = 50.0;

    // GPX descent starts. SST sessions start KnownOffsetMs earlier (GPX clock ahead).
    private static readonly int[] DescentStartSeconds = [120, 240, 360];

    [Fact]
    public void Estimate_RecoversKnownPositiveOffset()
    {
        var points = BuildPoints(flat: false);
        var intervals = SessionsForDescents(DescentStartSeconds);

        var estimated = TrackTimeOffsetEstimator.Estimate(points, intervals);

        Assert.Equal(KnownOffsetMs, estimated);
    }

    [Fact]
    public void Estimate_TooFewSessions_ReturnsNull()
    {
        var points = BuildPoints(flat: false);
        var intervals = SessionsForDescents(DescentStartSeconds.Take(2).ToArray());

        var estimated = TrackTimeOffsetEstimator.Estimate(points, intervals);

        Assert.Null(estimated);
    }

    [Fact]
    public void Estimate_FlatTrack_ReturnsNull()
    {
        var points = BuildPoints(flat: true);
        var intervals = SessionsForDescents(DescentStartSeconds);

        var estimated = TrackTimeOffsetEstimator.Estimate(points, intervals);

        Assert.Null(estimated);
    }

    // A realistic day profile: climb to the top, descend, climb again. The three descents drop by
    // different amounts, so the best lag per session is unique — repeated identical descents would
    // score the same at several lags and say nothing about the real offset.
    private static readonly double[] DropsMeters = [60.0, 45.0, 70.0];

    private static TrackPoints BuildPoints(bool flat)
    {
        var n = DurationSeconds + 1;
        var timeMs = new long[n];
        var lat = new double[n];
        var lon = new double[n];
        var ele = new double[n];
        for (var i = 0; i < n; i++)
        {
            timeMs[i] = BaseMs + i * 1000L;
            lat[i] = 47.0;
            lon[i] = 11.0;
            ele[i] = 400.0;
        }

        if (!flat)
        {
            for (var d = 0; d < DescentStartSeconds.Length; d++)
            {
                var startSec = DescentStartSeconds[d];
                var drop = DropsMeters[d];
                for (var i = 0; i <= DescentDurationSeconds; i++)
                {
                    var idx = startSec + i;
                    if (idx < 0 || idx >= n) continue;
                    ele[idx] = 400.0 - drop * (i / (double)DescentDurationSeconds);
                }

                // Climb back to the plateau over the following minute (uplift), so the profile
                // never jumps and the descent windows stay the only high-scoring alignments.
                var bottom = 400.0 - drop;
                const int climbSeconds = 60;
                for (var i = 1; i <= climbSeconds; i++)
                {
                    var idx = startSec + DescentDurationSeconds + i;
                    if (idx < 0 || idx >= n) continue;
                    ele[idx] = bottom + drop * (i / (double)climbSeconds);
                }
            }
        }

        return new TrackPoints
        {
            TimeMs = timeMs,
            Lat = lat,
            Lon = lon,
            Ele = ele
        };
    }

    private static WallClockSlice[] SessionsForDescents(IReadOnlyList<int> gpxDescentStartSeconds)
    {
        var slices = new WallClockSlice[gpxDescentStartSeconds.Count];
        for (var i = 0; i < gpxDescentStartSeconds.Count; i++)
        {
            // gpxTimeMs = sstWallClockMs + TimeOffsetMs, so SST start = GPX descent - offset.
            var sstStartMs = BaseMs + gpxDescentStartSeconds[i] * 1000L - KnownOffsetMs;
            slices[i] = new WallClockSlice(sstStartMs, DescentDurationSeconds * 1000L, 0);
        }

        return slices;
    }
}
