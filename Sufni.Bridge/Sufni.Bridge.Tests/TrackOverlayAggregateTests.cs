using Sufni.Bridge.Models;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Tests;

public class TrackOverlayAggregateTests
{
    private const int StartUnix = 1_700_000_000;

    private static SessionTrack TwoPointTrack(
        double lat0, double lon0, double lat1, double lon1, int durationSeconds = 1)
    {
        var startMs = (long)StartUnix * 1000;
        var endMs = startMs + (long)durationSeconds * 1000;
        var points = new TrackPoints
        {
            TimeMs = [startMs, endMs],
            Lat = [lat0, lat1],
            Lon = [lon0, lon1],
            Ele = [0, 0]
        };
        return SessionTrack.FromSession(points, StartUnix, durationSeconds);
    }

    private static TelemetryData Telemetry(
        int sampleRate,
        bool frontPresent = false,
        double[]? frontTravel = null,
        double[]? frontVelocity = null,
        bool rearPresent = false,
        double[]? rearTravel = null,
        double[]? rearVelocity = null,
        double? maxFrontTravel = null,
        double? maxRearTravel = null,
        double? wheelbase = null)
    {
        Linkage? linkage = null;
        if (maxFrontTravel.HasValue || maxRearTravel.HasValue || wheelbase.HasValue)
        {
            linkage = new Linkage();
            if (maxFrontTravel.HasValue) linkage.MaxFrontTravel = maxFrontTravel.Value;
            if (maxRearTravel.HasValue) linkage.MaxRearTravel = maxRearTravel.Value;
            if (wheelbase.HasValue) linkage.Wheelbase = wheelbase.Value;
        }

        return new TelemetryData
        {
            SampleRate = sampleRate,
            Linkage = linkage!,
            Front = new Suspension
            {
                Present = frontPresent,
                Travel = frontTravel ?? [],
                Velocity = frontVelocity ?? []
            },
            Rear = new Suspension
            {
                Present = rearPresent,
                Travel = rearTravel ?? [],
                Velocity = rearVelocity ?? []
            }
        };
    }

    private static double EdgeValue(
        double[] velocity, TrackOverlayAggregate aggregate, TrackOverlayMetric metric = TrackOverlayMetric.FrontCompression)
    {
        var data = Telemetry(
            sampleRate: 10,
            frontPresent: true,
            frontTravel: velocity,
            frontVelocity: velocity);
        var track = TwoPointTrack(47.0, 11.0, 47.01, 11.01);
        var overlay = TrackOverlaySampler.Build(track, data, metric, aggregate);
        Assert.NotNull(overlay);
        Assert.Equal(aggregate, overlay.Aggregate);
        return overlay.SegmentPairValues[0][0];
    }

    [Fact]
    public void Compression_MaxAvgP95_IgnoreNegatives()
    {
        // Positives only: 1, 3, 5, 7, 9, 6, 4. Sorted for P95: 1, 3, 4, 5, 6, 7, 9.
        var velocity = new double[] { 1, -20, 3, -30, 5, 7, -40, 9, 6, 4 };

        Assert.Equal(9.0, EdgeValue(velocity, TrackOverlayAggregate.Max), 6);
        Assert.Equal(5.0, EdgeValue(velocity, TrackOverlayAggregate.Avg), 6); // 35 / 7

        // pos = (7-1)*0.95 = 5.7 → interpolate between 7 and 9 → 8.4
        Assert.Equal(8.4, EdgeValue(velocity, TrackOverlayAggregate.P95), 6);
    }

    [Fact]
    public void Compression_ReboundOnly_IsZeroForAllAggregates()
    {
        var velocity = new double[] { -1, -2, -3, -4, -5, -6, -7, -8, -9, -10 };

        Assert.Equal(0.0, EdgeValue(velocity, TrackOverlayAggregate.Max), 6);
        Assert.Equal(0.0, EdgeValue(velocity, TrackOverlayAggregate.Avg), 6);
        Assert.Equal(0.0, EdgeValue(velocity, TrackOverlayAggregate.P95), 6);
    }

    [Fact]
    public void Compression_NoFiniteSamples_IsNaN()
    {
        var velocity = new double[]
        {
            double.NaN, double.PositiveInfinity, double.NegativeInfinity,
            double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN
        };

        Assert.True(double.IsNaN(EdgeValue(velocity, TrackOverlayAggregate.Max)));
        Assert.True(double.IsNaN(EdgeValue(velocity, TrackOverlayAggregate.Avg)));
        Assert.True(double.IsNaN(EdgeValue(velocity, TrackOverlayAggregate.P95)));
    }

    [Fact]
    public void TravelPercent_MaxAndAvg_UseAbsoluteTravel()
    {
        var travel = new double[] { -10, -20, -30, -40, -50, 60, 70, 80, 90, 100 };
        const double maxTravel = 200.0;
        var data = Telemetry(
            sampleRate: 10,
            frontPresent: true,
            frontTravel: travel,
            maxFrontTravel: maxTravel);
        var track = TwoPointTrack(47.0, 11.0, 47.01, 11.01);

        var maxOverlay = TrackOverlaySampler.Build(track, data, TrackOverlayMetric.FrontTravel, TrackOverlayAggregate.Max);
        Assert.NotNull(maxOverlay);
        Assert.Equal(50.0, maxOverlay.SegmentPairValues[0][0], 6); // |100| / 200 * 100

        var avgOverlay = TrackOverlaySampler.Build(track, data, TrackOverlayMetric.FrontTravel, TrackOverlayAggregate.Avg);
        Assert.NotNull(avgOverlay);
        Assert.Equal(27.5, avgOverlay.SegmentPairValues[0][0], 6); // mean(|travel|) / 200 * 100
    }

    [Fact]
    public void SupportsAggregate_FalseForGpsSpeedAndPitch_TrueOtherwise()
    {
        Assert.False(TrackOverlaySampler.SupportsAggregate(TrackOverlayMetric.GpsSpeed));
        Assert.False(TrackOverlaySampler.SupportsAggregate(TrackOverlayMetric.Pitch));
        Assert.True(TrackOverlaySampler.SupportsAggregate(TrackOverlayMetric.FrontTravel));
        Assert.True(TrackOverlaySampler.SupportsAggregate(TrackOverlayMetric.RearTravel));
        Assert.True(TrackOverlaySampler.SupportsAggregate(TrackOverlayMetric.FrontCompression));
        Assert.True(TrackOverlaySampler.SupportsAggregate(TrackOverlayMetric.RearCompression));
        Assert.True(TrackOverlaySampler.SupportsAggregate(TrackOverlayMetric.Impact));
    }

    [Fact]
    public void Build_GpsSpeed_StillStoresPassedAggregate()
    {
        var track = TwoPointTrack(0.0, 0.0, 0.001, 0.0);
        var overlay = TrackOverlaySampler.Build(
            track, data: null, TrackOverlayMetric.GpsSpeed, TrackOverlayAggregate.Avg);
        Assert.NotNull(overlay);
        Assert.Equal(TrackOverlayAggregate.Avg, overlay.Aggregate);
        Assert.True(double.IsFinite(overlay.SegmentPairValues[0][0]));
    }

    private static SessionTrack GpsSpeedTrack(
        int pointCount, double dLat, int? outlierEdge = null, double outlierDLat = 0.001)
    {
        var startMs = (long)StartUnix * 1000;
        var timeMs = new long[pointCount];
        var lat = new double[pointCount];
        var lon = new double[pointCount];
        var ele = new double[pointCount];
        var currentLat = 0.0;
        for (var i = 0; i < pointCount; i++)
        {
            timeMs[i] = startMs + i * 1000L;
            lat[i] = currentLat;
            // SessionTrack.Build decimates colinear vertices. A straight north line would
            // collapse to two points and drop the outlier edge, so longitude zigzags.
            lon[i] = (i & 1) == 0 ? 0.0 : 0.0001;
            if (i + 1 < pointCount)
                currentLat += outlierEdge == i ? outlierDLat : dLat;
        }

        var points = new TrackPoints
        {
            TimeMs = timeMs,
            Lat = lat,
            Lon = lon,
            Ele = ele
        };
        return SessionTrack.FromSession(points, StartUnix, pointCount - 1);
    }

    [Fact]
    public void Build_GpsSpeed_ClampsScaleAt99thPercentileWhenOutlierPresent()
    {
        // 100 edges at ~40 km/h plus one ~400 km/h jump. The scale must end at the 99th
        // percentile so the outlier does not squash the colormap; the values stay intact.
        var track = GpsSpeedTrack(pointCount: 101, dLat: 0.0001, outlierEdge: 50);
        var overlay = TrackOverlaySampler.Build(track, data: null, TrackOverlayMetric.GpsSpeed);
        Assert.NotNull(overlay);

        var trueMax = overlay.SegmentPairValues.SelectMany(v => v).Where(double.IsFinite).Max();
        Assert.True(overlay.MaxIsClamped);
        Assert.True(overlay.Max < trueMax);
        Assert.True(overlay.Max > overlay.Min);
        Assert.Contains(trueMax, overlay.SegmentPairValues.SelectMany(v => v));
    }

    [Fact]
    public void Build_GpsSpeed_DoesNotClampWhenSpeedsAreUniform()
    {
        // One edge, no spread: the 99th percentile equals the single value, so the scale
        // is not clamped. A multi-edge zigzag still has tiny haversine differences and
        // would trip the p99 < max test even without a real outlier.
        var track = TwoPointTrack(0.0, 0.0, 0.0001, 0.0);
        var overlay = TrackOverlaySampler.Build(track, data: null, TrackOverlayMetric.GpsSpeed);
        Assert.NotNull(overlay);

        var trueMax = overlay.SegmentPairValues.SelectMany(v => v).Where(double.IsFinite).Max();
        Assert.False(overlay.MaxIsClamped);
        Assert.Equal(trueMax, overlay.Max, 6);
    }
}
