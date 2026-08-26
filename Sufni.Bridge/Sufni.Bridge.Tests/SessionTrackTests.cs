using Sufni.Bridge.Models;

namespace Sufni.Bridge.Tests;

public class SessionTrackTests
{
    private const int StartUnix = 1_700_000_000;

    private static TrackPoints LinearPoints(
        int count,
        long startUnixMs,
        long stepMs,
        double lat0,
        double lon0,
        double dLat,
        double dLon)
    {
        var timeMs = new long[count];
        var lat = new double[count];
        var lon = new double[count];
        var ele = new double[count];
        for (var i = 0; i < count; i++)
        {
            timeMs[i] = startUnixMs + i * stepMs;
            lat[i] = lat0 + i * dLat;
            lon[i] = lon0 + i * dLon;
        }

        return new TrackPoints
        {
            TimeMs = timeMs,
            Lat = lat,
            Lon = lon,
            Ele = ele
        };
    }

    [Fact]
    public void FromSession_ClipsWindow_InterpolatesEdges_SetsRelativeTime()
    {
        // Samples at t=0, 10, 20 s. Session [5, 15] s.
        var points = LinearPoints(
            count: 3,
            startUnixMs: (long)StartUnix * 1000,
            stepMs: 10_000,
            lat0: 47.0,
            lon0: 11.0,
            dLat: 0.1,
            dLon: 0.1);

        var track = SessionTrack.FromSession(points, StartUnix + 5, 10);

        Assert.False(track.IsEmpty);
        var segment = Assert.Single(track.Segments);
        Assert.True(segment.TimeSeconds.Length >= 2);
        Assert.Equal(0.0, segment.TimeSeconds[0], 6);
        Assert.Equal(10.0, segment.TimeSeconds[^1], 6);
        Assert.Equal(47.05, segment.Lat[0], 6);
        Assert.Equal(11.05, segment.Lon[0], 6);
        Assert.Equal(47.15, segment.Lat[^1], 6);
        Assert.Equal(11.15, segment.Lon[^1], 6);
        Assert.True(SessionTrack.TryToWebMercator(47.05, 11.05, out var x0, out var y0));
        Assert.Equal(x0, segment.X[0], 6);
        Assert.Equal(y0, segment.Y[0], 6);
    }

    [Fact]
    public void FromSession_WindowCompletelyOutside_ReturnsEmpty()
    {
        var points = LinearPoints(
            count: 3,
            startUnixMs: (long)StartUnix * 1000,
            stepMs: 1_000,
            lat0: 47.0,
            lon0: 11.0,
            dLat: 0.001,
            dLon: 0.001);

        var after = SessionTrack.FromSession(points, StartUnix + 60, 10);
        var before = SessionTrack.FromSession(points, StartUnix - 30, 10);

        Assert.True(after.IsEmpty);
        Assert.Empty(after.Segments);
        Assert.True(before.IsEmpty);
        Assert.Empty(before.Segments);
    }

    [Fact]
    public void Build_CombinedSlices_YieldsSeparateSegments()
    {
        var points = LinearPoints(
            count: 21,
            startUnixMs: (long)StartUnix * 1000,
            stepMs: 1_000,
            lat0: 47.0,
            lon0: 11.0,
            dLat: 0.001,
            dLon: 0.001);

        var slices = new WallClockSlice[]
        {
            new((long)StartUnix * 1000, 5_000, 0),
            new((long)(StartUnix + 10) * 1000, 5_000, 5)
        };

        var track = SessionTrack.Build(points, slices);

        Assert.False(track.IsEmpty);
        Assert.Equal(2, track.Segments.Count);

        var first = track.Segments[0];
        var second = track.Segments[1];
        Assert.Equal(0.0, first.TimeSeconds[0], 6);
        Assert.Equal(5.0, first.TimeSeconds[^1], 6);
        Assert.Equal(5.0, second.TimeSeconds[0], 6);
        Assert.Equal(10.0, second.TimeSeconds[^1], 6);

        Assert.Equal(47.0, first.Lat[0], 5);
        Assert.Equal(47.005, first.Lat[^1], 5);
        Assert.Equal(47.010, second.Lat[0], 5);
        Assert.Equal(47.015, second.Lat[^1], 5);
        Assert.NotEqual(first.Lat[^1], second.Lat[0]);
    }

    [Fact]
    public void Build_SliceWithoutPoints_IsOmittedFromSegments()
    {
        var points = LinearPoints(
            count: 6,
            startUnixMs: (long)StartUnix * 1000,
            stepMs: 1_000,
            lat0: 10.0,
            lon0: 20.0,
            dLat: 0.01,
            dLon: 0.01);

        var slices = new WallClockSlice[]
        {
            new((long)StartUnix * 1000, 5_000, 0),
            new((long)(StartUnix + 100) * 1000, 5_000, 5)
        };

        var track = SessionTrack.Build(points, slices);

        var segment = Assert.Single(track.Segments);
        Assert.Equal(0.0, segment.TimeSeconds[0], 6);
    }

    [Fact]
    public void Build_NonZeroOffset_ShiftsTimeWindow_DoesNotMutateInputTimeMs()
    {
        var points = LinearPoints(
            count: 21,
            startUnixMs: (long)StartUnix * 1000,
            stepMs: 1_000,
            lat0: 47.0,
            lon0: 11.0,
            dLat: 0.001,
            dLon: 0.001);
        var originalTimeMs = (long[])points.TimeMs.Clone();
        var slices = new WallClockSlice[]
        {
            new((long)StartUnix * 1000, 5_000, 0)
        };

        const long offsetMs = 1_000;
        var track = SessionTrack.Build(points, slices, offsetMs);

        Assert.Equal(originalTimeMs, points.TimeMs);
        Assert.False(track.IsEmpty);
        var segment = Assert.Single(track.Segments);
        Assert.Equal(0.0, segment.TimeSeconds[0], 6);
        Assert.Equal(5.0, segment.TimeSeconds[^1], 6);
        // gpxTimeMs = sstWallClockMs + offset, so SST [0, 5] s maps to GPX [1, 6] s.
        Assert.Equal(47.001, segment.Lat[0], 5);
        Assert.Equal(47.006, segment.Lat[^1], 5);
    }

    [Fact]
    public void TryToWebMercator_KnownReferenceValues()
    {
        Assert.True(SessionTrack.TryToWebMercator(0, 0, out var x0, out var y0));
        Assert.Equal(0.0, x0, 9);
        Assert.Equal(0.0, y0, 9);

        Assert.True(SessionTrack.TryToWebMercator(0, 180, out var x180, out var y180));
        Assert.Equal(6378137.0 * Math.PI, x180, 3);
        Assert.InRange(x180, 20_037_508.0, 20_037_509.0);
        Assert.Equal(0.0, y180, 9);

        Assert.True(SessionTrack.TryToWebMercator(0, -180, out var xNeg, out _));
        Assert.Equal(-6378137.0 * Math.PI, xNeg, 3);

        Assert.False(SessionTrack.TryToWebMercator(90, 0, out _, out _));
        Assert.False(SessionTrack.TryToWebMercator(-90, 0, out _, out _));
        Assert.False(SessionTrack.TryToWebMercator(0, 180.1, out _, out _));
        Assert.False(SessionTrack.TryToWebMercator(0, -180.1, out _, out _));
    }

    // --- BoundsForWindow -----------------------------------------------------

    // ~0.001 deg of longitude at 47N is roughly 75 m in Web Mercator easting, so a 60 s track at
    // one point per second spans far more than MinFocusSpanMeters and the guard never kicks in.
    private static SessionTrack LongTrack() =>
        SessionTrack.FromSession(
            LinearPoints(61, (long)StartUnix * 1000, 1000, 47.0, 11.0, 0.0, 0.001),
            StartUnix,
            60);

    [Fact]
    public void BoundsForWindow_SubRange_IsSmallerThanWholeTrack()
    {
        var track = LongTrack();
        var all = track.Bounds;

        var part = track.BoundsForWindow(20, 30);

        Assert.NotNull(part);
        Assert.True(part!.Width < all.Width);
        Assert.True(part.MinX > all.MinX);
        Assert.True(part.MaxX < all.MaxX);
    }

    [Fact]
    public void BoundsForWindow_OutsideTrack_ReturnsNull()
    {
        var track = LongTrack();

        Assert.Null(track.BoundsForWindow(500, 510));
    }

    [Fact]
    public void BoundsForWindow_TinyRange_IsExpandedToMinimumSpan()
    {
        var track = LongTrack();

        // A hundredth of a second covers well under a metre of track.
        var box = track.BoundsForWindow(30.00, 30.01);

        Assert.NotNull(box);
        Assert.Equal(SessionTrack.MinFocusSpanMeters, box!.Width, 6);
        Assert.Equal(SessionTrack.MinFocusSpanMeters, box.Height, 6);
    }
}
