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
        Assert.False(TrackOverlaySampler.IsAvailable(TrackOverlayMetric.Pitch, empty));

        var frontOnly = Telemetry(
            sampleRate: 10,
            frontPresent: true,
            frontTravel: [1.0, 2.0],
            frontVelocity: [3.0]);
        Assert.True(TrackOverlaySampler.IsAvailable(TrackOverlayMetric.FrontTravel, frontOnly));
        Assert.True(TrackOverlaySampler.IsAvailable(TrackOverlayMetric.FrontVelocity, frontOnly));
        Assert.False(TrackOverlaySampler.IsAvailable(TrackOverlayMetric.RearTravel, frontOnly));
        Assert.False(TrackOverlaySampler.IsAvailable(TrackOverlayMetric.Pitch, frontOnly));

        var presentEmptyArray = Telemetry(
            sampleRate: 10,
            frontPresent: true,
            frontTravel: [],
            rearPresent: true,
            rearTravel: [1.0]);
        Assert.False(TrackOverlaySampler.IsAvailable(TrackOverlayMetric.FrontTravel, presentEmptyArray));
        Assert.False(TrackOverlaySampler.IsAvailable(TrackOverlayMetric.Pitch, presentEmptyArray));

        // Pitch needs front+rear travel AND a wheelbase (chassis geometry).
        var bothNoWheelbase = Telemetry(
            sampleRate: 10,
            frontPresent: true,
            frontTravel: [1.0],
            rearPresent: true,
            rearTravel: [2.0]);
        Assert.False(TrackOverlaySampler.IsAvailable(TrackOverlayMetric.Pitch, bothNoWheelbase));

        var both = Telemetry(
            sampleRate: 10,
            frontPresent: true,
            frontTravel: [1.0],
            rearPresent: true,
            rearTravel: [2.0],
            wheelbase: 1200);
        Assert.True(TrackOverlaySampler.IsAvailable(TrackOverlayMetric.Pitch, both));
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
            rearTravel: [2.0],
            wheelbase: 1200);
        Assert.Equal(
            [
                TrackOverlayMetric.GpsSpeed,
                TrackOverlayMetric.FrontTravel,
                TrackOverlayMetric.RearTravel,
                TrackOverlayMetric.Pitch
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
    public void Build_Travel_IsAbsoluteMeanAsPercentOfMaxTravel()
    {
        var travel = new double[] { -10, -20, -30, -40, -50, 60, 70, 80, 90, 100 };
        var absMean = travel.Select(Math.Abs).Average();      // 55
        const double maxTravel = 200.0;
        var expectedPercent = absMean / maxTravel * 100.0;    // 27.5
        var data = Telemetry(sampleRate: 10, frontPresent: true, frontTravel: travel, maxFrontTravel: maxTravel);
        var track = TwoPointTrack(47.0, 11.0, 47.01, 11.01);

        var overlay = TrackOverlaySampler.Build(track, data, TrackOverlayMetric.FrontTravel);

        Assert.NotNull(overlay);
        Assert.Equal(TrackOverlayMetric.FrontTravel, overlay.Metric);
        Assert.Equal("%", overlay.Unit);
        Assert.Single(overlay.SegmentPairValues);
        Assert.Equal(expectedPercent, overlay.SegmentPairValues[0][0], 6);
        Assert.Equal(expectedPercent, overlay.Min, 6);
        Assert.Equal(expectedPercent, overlay.Max, 6);
    }

    [Fact]
    public void Build_Velocity_IsAbsoluteMean_NotSymmetric()
    {
        var velocity = new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var data = Telemetry(
            sampleRate: 10,
            frontPresent: true,
            frontTravel: velocity,
            frontVelocity: velocity);
        var track = TwoPointTrack(47.0, 11.0, 47.01, 11.01);

        var vel = TrackOverlaySampler.Build(track, data, TrackOverlayMetric.FrontVelocity);
        Assert.NotNull(vel);
        Assert.Equal("mm/s", vel.Unit);
        Assert.Equal(5.5, vel.SegmentPairValues[0][0], 6);   // mean |v|
        Assert.Equal(5.5, vel.Min, 6);
        Assert.Equal(5.5, vel.Max, 6);
    }

    [Fact]
    public void Build_Pitch_IsDegreesAndSymmetricAroundZero()
    {
        var frontTravel = new double[] { 0, 5, 10, 15, 20, 20, 15, 10, 5, 0 };
        var rearTravel = new double[] { 0, 2, 4, 6, 8, 8, 6, 4, 2, 0 };
        var data = Telemetry(
            sampleRate: 10,
            frontPresent: true,
            frontTravel: frontTravel,
            rearPresent: true,
            rearTravel: rearTravel,
            wheelbase: 1200);
        var track = TwoPointTrack(47.0, 11.0, 47.01, 11.01);

        var pitch = TrackOverlaySampler.Build(track, data, TrackOverlayMetric.Pitch);
        Assert.NotNull(pitch);
        Assert.Equal(TrackOverlayMetric.Pitch, pitch.Metric);
        Assert.Equal("°", pitch.Unit);
        // Signed metric: the colour scale is normalised symmetrically around zero.
        Assert.Equal(-pitch.Max, pitch.Min, 6);
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
