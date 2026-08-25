using System;
using System.Collections.Generic;
using Sufni.Bridge.Plots;

namespace Sufni.Bridge.Models;

public readonly record struct WallClockSlice(long StartUnixMs, long DurationMs, double SessionStartSeconds);

public sealed class MapBounds
{
    public double MinX { get; init; }
    public double MinY { get; init; }
    public double MaxX { get; init; }
    public double MaxY { get; init; }

    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;

    public MapBounds Pad(double fraction)
    {
        var padX = Width * fraction;
        var padY = Height * fraction;
        if (padX <= 0) padX = 1;
        if (padY <= 0) padY = 1;
        return new MapBounds
        {
            MinX = MinX - padX,
            MinY = MinY - padY,
            MaxX = MaxX + padX,
            MaxY = MaxY + padY
        };
    }

    public static MapBounds FromPoints(IReadOnlyList<TrackSegment> segments)
    {
        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;
        foreach (var segment in segments)
        {
            for (var i = 0; i < segment.X.Length; i++)
            {
                var x = segment.X[i];
                var y = segment.Y[i];
                if (double.IsNaN(x) || double.IsNaN(y)) continue;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }

        if (double.IsPositiveInfinity(minX))
            return new MapBounds();

        return new MapBounds { MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY };
    }
}

public sealed class ZoomWindow
{
    public double StartSeconds { get; init; }
    public double EndSeconds { get; init; }
}

public sealed class TrackSegment
{
    public double[] X { get; }
    public double[] Y { get; }
    public double[] TimeSeconds { get; }
    public double[] Lat { get; }
    public double[] Lon { get; }

    public TrackSegment(double[] x, double[] y, double[] timeSeconds, double[] lat, double[] lon)
    {
        X = x;
        Y = y;
        TimeSeconds = timeSeconds;
        Lat = lat;
        Lon = lon;
    }
}

public sealed class SessionTrack
{
    public static SessionTrack Empty { get; } = new([]);

    public IReadOnlyList<TrackSegment> Segments { get; }
    public MapBounds Bounds { get; }
    public bool IsEmpty => Segments.Count == 0;

    private SessionTrack(IReadOnlyList<TrackSegment> segments)
    {
        Segments = segments;
        Bounds = MapBounds.FromPoints(segments);
    }

    public static SessionTrack Build(TrackPoints points, IReadOnlyList<WallClockSlice> slices)
    {
        if (points.TimeMs.Length < 2 || slices.Count == 0)
            return Empty;

        var segments = new List<TrackSegment>();
        foreach (var slice in slices)
        {
            if (slice.DurationMs <= 0) continue;
            var segment = Slice(points, slice);
            if (segment is not null)
                segments.Add(Decimate(segment));
        }

        return segments.Count == 0 ? Empty : new SessionTrack(segments);
    }

    public static SessionTrack FromSession(TrackPoints points, int startUnixSeconds, int durationSeconds)
    {
        return Build(points, [
            new WallClockSlice((long)startUnixSeconds * 1000, (long)durationSeconds * 1000, 0)
        ]);
    }

    /// <summary>
    /// Web Mercator in meters. Same formula as sst/dashboard/app/telemetry/map.py:18-26.
    /// Returns easting (x), northing (y).
    /// </summary>
    public static bool TryToWebMercator(double lat, double lon, out double x, out double y)
    {
        x = 0;
        y = 0;
        if (Math.Abs(lon) > 180 || Math.Abs(lat) >= 90)
            return false;

        const double degToRad = 0.017453292519943295;
        var num = lon * degToRad;
        x = 6378137.0 * num;
        var a = lat * degToRad;
        y = 3189068.5 * Math.Log((1.0 + Math.Sin(a)) / (1.0 - Math.Sin(a)));
        return true;
    }

    private static TrackSegment? Slice(TrackPoints points, WallClockSlice slice)
    {
        var times = points.TimeMs;
        var n = times.Length;
        var startMs = slice.StartUnixMs;
        var endMs = slice.StartUnixMs + slice.DurationMs;

        var firstInside = -1;
        var lastInside = -1;
        for (var i = 0; i < n; i++)
        {
            if (times[i] < startMs) continue;
            if (times[i] > endMs) break;
            if (firstInside < 0) firstInside = i;
            lastInside = i;
        }

        var before = firstInside >= 0 ? firstInside - 1 : LastIndexBefore(times, startMs);
        var after = lastInside >= 0 ? lastInside + 1 : FirstIndexAfter(times, endMs);
        if (after < n && times[after] <= endMs) after = lastInside + 1;

        if (firstInside < 0)
        {
            before = LastIndexBefore(times, startMs);
            after = FirstIndexAfter(times, endMs);
            if (before < 0 || after >= n)
                return null;
        }

        var xs = new List<double>();
        var ys = new List<double>();
        var ts = new List<double>();
        var lats = new List<double>();
        var lons = new List<double>();

        void Add(long timeMs, double lat, double lon)
        {
            if (!TryToWebMercator(lat, lon, out var x, out var y))
                return;
            xs.Add(x);
            ys.Add(y);
            ts.Add((timeMs - startMs) / 1000.0 + slice.SessionStartSeconds);
            lats.Add(lat);
            lons.Add(lon);
        }

        if (before >= 0 && (firstInside < 0 || times[firstInside] > startMs))
        {
            var afterIdx = firstInside >= 0 ? firstInside : after;
            if (afterIdx >= 0 && afterIdx < n && times[before] < startMs)
            {
                Interpolate(points, before, afterIdx, startMs, out var lat, out var lon);
                Add(startMs, lat, lon);
            }
        }

        if (firstInside >= 0)
        {
            for (var i = firstInside; i <= lastInside; i++)
                Add(times[i], points.Lat[i], points.Lon[i]);
        }

        var afterEdge = firstInside < 0 ? after : lastInside + 1;
        if (afterEdge < n && (lastInside < 0 || times[lastInside] < endMs))
        {
            var beforeIdx = lastInside >= 0 ? lastInside : before;
            if (beforeIdx >= 0 && times[afterEdge] > endMs)
            {
                Interpolate(points, beforeIdx, afterEdge, endMs, out var lat, out var lon);
                Add(endMs, lat, lon);
            }
        }

        return xs.Count < 2
            ? null
            : new TrackSegment(xs.ToArray(), ys.ToArray(), ts.ToArray(), lats.ToArray(), lons.ToArray());
    }

    private static int LastIndexBefore(long[] times, long t)
    {
        var idx = -1;
        for (var i = 0; i < times.Length; i++)
        {
            if (times[i] >= t) break;
            idx = i;
        }

        return idx;
    }

    private static int FirstIndexAfter(long[] times, long t)
    {
        for (var i = 0; i < times.Length; i++)
        {
            if (times[i] > t) return i;
        }

        return times.Length;
    }

    private static void Interpolate(TrackPoints points, int i0, int i1, long timeMs,
        out double lat, out double lon)
    {
        var t0 = points.TimeMs[i0];
        var t1 = points.TimeMs[i1];
        var span = t1 - t0;
        var frac = span == 0 ? 0 : (double)(timeMs - t0) / span;
        lat = points.Lat[i0] + frac * (points.Lat[i1] - points.Lat[i0]);
        lon = points.Lon[i0] + frac * (points.Lon[i1] - points.Lon[i0]);
    }

    private static TrackSegment Decimate(TrackSegment segment)
    {
        var (xs, ys, ts, lat, lon) = PathDecimation.DecimatePolyline(
            segment.X, segment.Y, segment.TimeSeconds, segment.Lat, segment.Lon);
        return new TrackSegment(xs, ys, ts, lat, lon);
    }
}
