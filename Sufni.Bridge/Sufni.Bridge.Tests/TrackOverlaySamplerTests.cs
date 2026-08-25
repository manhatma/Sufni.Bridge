using Sufni.Bridge.Models;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Tests;

public class TrackOverlaySamplerTests
{
    private const int StartUnix = 1_700_000_000;
    private const double EarthRadiusMeters = 6_371_000.0;

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
        double[]? rearVelocity = null)
    {
        return new TelemetryData
        {
            SampleRate = sampleRate,
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

    private static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double degToRad = Math.PI / 180.0;
        var phi1 = lat1 * degToRad;
        var phi2 = lat2 * degToRad;
        var dPhi = (lat2 - lat1) * degToRad;
        var dLam = (lon2 - lon1) * degToRad;
        var sinDPhi = Math.Sin(dPhi / 2.0);
        var sinDLam = Math.Sin(dLam / 2.0);
        var a = sinDPhi * sinDPhi + Math.Cos(phi1) * Math.Cos(phi2) * sinDLam * sinDLam;
        return 2.0 * EarthRadiusMeters * Math.Atan2(Math.Sqrt(a), Math.Sqrt(Math.Max(0.0, 1.0 - a)));
    }

    [Fact]
    public void IsAvailable_GpsSpeedAlways_OthersNeedPresentNonEmptyArrays()
    {
        Assert.True(TrackOverlaySampler.IsAvailable(TrackOverlayMetric.GpsSpeed, null));

        var empty = Telemetry(sampleRate: 10);
        Assert.True(TrackOverlaySampler.IsAvailable(TrackOverlayMetric.GpsSpeed, empty));
        Assert.False(TrackOverlaySampler.IsAvailable(TrackOverlayMetric.FrontTravel, empty));
        Assert.False(TrackOverlaySampler.IsAvailable(TrackOverlayMetric.RearTravel, empty));
        Assert.False(TrackOverlaySampler.IsAvailable(TrackOverlayMetric.FrontVelocity, empty));
        Assert.False(TrackOverlaySampler.IsAvailable(TrackOverlayMetric.RearVelocity, empty));
        Assert.False(TrackOverlaySampler.IsAvailable(TrackOverlayMetric.Balance, empty));

        var frontOnly = Telemetry(
            sampleRate: 10,
            frontPresent: true,
            frontTravel: [1.0, 2.0],
            frontVelocity: [3.0]);
        Assert.True(TrackOverlaySampler.IsAvailable(TrackOverlayMetric.FrontTravel, frontOnly));
        Assert.True(TrackOverlaySampler.IsAvailable(TrackOverlayMetric.FrontVelocity, frontOnly));
        Assert.False(TrackOverlaySampler.IsAvailable(TrackOverlayMetric.RearTravel, frontOnly));
        Assert.False(TrackOverlaySampler.IsAvailable(TrackOverlayMetric.Balance, frontOnly));

        var presentEmptyArray = Telemetry(
            sampleRate: 10,
            frontPresent: true,
            frontTravel: [],
            rearPresent: true,
            rearTravel: [1.0]);
        Assert.False(TrackOverlaySampler.IsAvailable(TrackOverlayMetric.FrontTravel, presentEmptyArray));
        Assert.False(TrackOverlaySampler.IsAvailable(TrackOverlayMetric.Balance, presentEmptyArray));

        var both = Telemetry(
            sampleRate: 10,
            frontPresent: true,
            frontTravel: [1.0],
            rearPresent: true,
            rearTravel: [2.0]);
        Assert.True(TrackOverlaySampler.IsAvailable(TrackOverlayMetric.Balance, both));
    }

    [Fact]
    public void AvailableMetrics_ListsOnlyPresentChannelsPlusGpsSpeed()
    {
        Assert.Equal([TrackOverlayMetric.GpsSpeed], TrackOverlaySampler.AvailableMetrics(null));

        var none = Telemetry(sampleRate: 10);
        Assert.Equal([TrackOverlayMetric.GpsSpeed], TrackOverlaySampler.AvailableMetrics(none));

        var frontTravel = Telemetry(sampleRate: 10, frontPresent: true, frontTravel: [1.0]);
        Assert.Equal(
            [TrackOverlayMetric.GpsSpeed, TrackOverlayMetric.FrontTravel],
            TrackOverlaySampler.AvailableMetrics(frontTravel));

        var bothTravel = Telemetry(
            sampleRate: 10,
            frontPresent: true,
            frontTravel: [1.0],
            rearPresent: true,
            rearTravel: [2.0]);
        Assert.Equal(
            [
                TrackOverlayMetric.GpsSpeed,
                TrackOverlayMetric.FrontTravel,
                TrackOverlayMetric.RearTravel,
                TrackOverlayMetric.Balance
            ],
            TrackOverlaySampler.AvailableMetrics(bothTravel));
    }

    [Fact]
    public void Build_EmptyTrackOrUnavailableMetric_ReturnsNull()
    {
        var data = Telemetry(sampleRate: 10, frontPresent: true, frontTravel: [1.0]);
        Assert.Null(TrackOverlaySampler.Build(SessionTrack.Empty, data, TrackOverlayMetric.GpsSpeed));
        Assert.Null(TrackOverlaySampler.Build(TwoPointTrack(0, 0, 0.001, 0), data, TrackOverlayMetric.RearTravel));
    }

    [Fact]
    public void Build_TravelUsesAbsoluteMean_OverSampleInterval()
    {
        var travel = new double[] { -10, -20, -30, -40, -50, 60, 70, 80, 90, 100 };
        var expected = travel.Select(Math.Abs).Average();
        var data = Telemetry(sampleRate: 10, frontPresent: true, frontTravel: travel);
        var track = TwoPointTrack(47.0, 11.0, 47.01, 11.01);

        var overlay = TrackOverlaySampler.Build(track, data, TrackOverlayMetric.FrontTravel);

        Assert.NotNull(overlay);
        Assert.Equal(TrackOverlayMetric.FrontTravel, overlay.Metric);
        Assert.Equal("mm", overlay.Unit);
        Assert.Single(overlay.SegmentPairValues);
        Assert.Equal(expected, overlay.SegmentPairValues[0][0], 6);
        Assert.Equal(expected, overlay.Min, 6);
        Assert.Equal(expected, overlay.Max, 6);
    }

    [Fact]
    public void Build_VelocityAndBalance_AreSymmetricAroundZero()
    {
        var velocity = new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var frontTravel = new double[] { 20, 20, 20, 20, 20, 20, 20, 20, 20, 20 };
        var rearTravel = new double[] { 5, 5, 5, 5, 5, 5, 5, 5, 5, 5 };
        var data = Telemetry(
            sampleRate: 10,
            frontPresent: true,
            frontTravel: frontTravel,
            frontVelocity: velocity,
            rearPresent: true,
            rearTravel: rearTravel,
            rearVelocity: velocity);
        var track = TwoPointTrack(47.0, 11.0, 47.01, 11.01);

        var vel = TrackOverlaySampler.Build(track, data, TrackOverlayMetric.FrontVelocity);
        Assert.NotNull(vel);
        Assert.Equal(5.5, vel.SegmentPairValues[0][0], 6);
        Assert.Equal(-5.5, vel.Min, 6);
        Assert.Equal(5.5, vel.Max, 6);

        var balance = TrackOverlaySampler.Build(track, data, TrackOverlayMetric.Balance);
        Assert.NotNull(balance);
        Assert.Equal(15.0, balance.SegmentPairValues[0][0], 6);
        Assert.Equal(-15.0, balance.Min, 6);
        Assert.Equal(15.0, balance.Max, 6);
    }

    [Fact]
    public void Build_GpsSpeed_MatchesHaversine()
    {
        const double lat0 = 0.0;
        const double lon0 = 0.0;
        const double lat1 = 0.001;
        const double lon1 = 0.0;
        var track = TwoPointTrack(lat0, lon0, lat1, lon1, durationSeconds: 1);
        var expectedKmh = HaversineMeters(lat0, lon0, lat1, lon1) / 1.0 * 3.6;

        var overlay = TrackOverlaySampler.Build(track, data: null, TrackOverlayMetric.GpsSpeed);

        Assert.NotNull(overlay);
        Assert.Equal(TrackOverlayMetric.GpsSpeed, overlay.Metric);
        Assert.Equal("km/h", overlay.Unit);
        Assert.Equal(expectedKmh, overlay.SegmentPairValues[0][0], 6);
        Assert.Equal(expectedKmh, overlay.Min, 6);
        Assert.Equal(expectedKmh, overlay.Max, 6);
        Assert.InRange(overlay.SegmentPairValues[0][0], expectedKmh * 0.99, expectedKmh * 1.01);
        Assert.InRange(expectedKmh, 390, 410);
    }
}
